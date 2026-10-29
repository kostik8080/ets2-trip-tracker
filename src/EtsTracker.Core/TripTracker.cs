using System.Text.Json;

namespace EtsTracker;

/// <summary>
/// Следит за телеметрией: ловит начало и конец заказа, копит пробег,
/// расход топлива, штрафы, превышения. По завершении отдаёт TripResult.
/// Незавершённый рейс сохраняется в файл и восстанавливается после перезапуска.
/// </summary>
public sealed class TripTracker
{
    private readonly CargoCatalog _catalog;
    private readonly ScoreCalculator _calculator;
    private readonly ScoreSettings _settings;
    private readonly object _lock = new();

    private TelemetryFrame? _prev;
    private ActiveTrip? _trip;
    private DateTime? _offJobSince;

    /// <summary>Файл незавершённого рейса (data/active_trip.json); null — не сохранять (демо).</summary>
    private readonly string? _statePath;
    /// <summary>Отложенный рейс: ждём, не вернётся ли этот заказ (после перезапуска или смены сохранения).</summary>
    private ActiveTripState? _resumable;
    private DateTime _lastSave;

    public event Action<ActiveTrip>? TripStarted;
    public event Action<TripResult>? TripFinished;
    public event Action<string>? Notice;

    public TelemetryFrame? LastFrame { get; private set; }
    public DateTime LastFrameAt { get; private set; }
    public ActiveTrip? Current { get { lock (_lock) return _trip; } }

    public TripTracker(CargoCatalog catalog, ScoreSettings settings, string? statePath = null)
    {
        _catalog = catalog;
        _settings = settings;
        _calculator = new ScoreCalculator(settings);
        _statePath = statePath;
        _resumable = LoadState();
    }

    public void Process(TelemetryFrame f)
    {
        TripResult? finished = null;
        ActiveTrip? started = null;

        lock (_lock)
        {
            LastFrame = f;
            LastFrameAt = DateTime.Now;
            if (!f.SdkActive) return;

            var prev = _prev;
            _prev = f;

            // --- Начало заказа (или приложение запущено посреди заказа) ---
            //     Стартуем только по «фронту» OnJob, чтобы после доставки,
            //     пока флаг ещё не сброшен, не открылся фантомный рейс.
            bool jobAppeared = f.OnJob && (prev == null || !prev.OnJob);
            if (jobAppeared && _trip == null && !f.JobDeliveredFlag && !f.JobCancelledFlag)
            {
                _trip = ActiveTrip.Begin(f, midJob: prev == null);
                started = _trip;
            }

            if (_trip != null)
            {
                _trip.Update(f, prev, _settings);
                TryResume(f);

                if (Rising(prev, f, x => x.FinedFlag))
                {
                    _trip.FinesCount++;
                    _trip.FinesTotal += f.FineAmount;
                    Notice?.Invoke($"🚨 Штраф: {f.FineOffence}, €{f.FineAmount}");
                    SaveState(force: true);
                }

                if (Rising(prev, f, x => x.TollgateFlag))
                    _trip.TollsTotal += f.TollAmount;

                if (Rising(prev, f, x => x.JobDeliveredFlag))
                    finished = Finish(f, TripStatus.Delivered);
                else if (Rising(prev, f, x => x.JobCancelledFlag))
                    finished = Finish(f, TripStatus.Cancelled);
                else if (!f.OnJob)
                {
                    // Заказ пропал без доставки/отмены (загрузили другое сохранение и т.п.).
                    // Ждём 5 секунд на случай, если событие доставки придёт чуть позже.
                    _offJobSince ??= DateTime.Now;
                    if (DateTime.Now - _offJobSince > TimeSpan.FromSeconds(5))
                    {
                        // Не выбрасываем: если этот заказ вернётся (загрузили сохранение), рейс продолжится
                        _resumable = _trip.Snapshot();
                        SaveState(force: true, _resumable);
                        _trip = null;
                        Notice?.Invoke("ℹ Заказ пропал без доставки (выход в меню или смена сохранения?) — " +
                                       "рейс отложен и продолжится, если вернуться к этому заказу.");
                    }
                }
                else _offJobSince = null;

                if (_trip != null) SaveState(force: false);
            }
            if (_trip == null) _offJobSince = null;
        }

        // События вызываем вне блокировки
        if (started != null) TripStarted?.Invoke(started);
        if (finished != null) TripFinished?.Invoke(finished);
    }

    /// <summary>
    /// Если начавшийся рейс — тот же заказ, что был отложен, продолжаем его с сохранённого места.
    /// Проверяем, когда игра уже заполнила данные заказа (груз, города, доход).
    /// </summary>
    private void TryResume(TelemetryFrame f)
    {
        if (_resumable == null || string.IsNullOrEmpty(f.CargoId)) return;
        var saved = _resumable;
        _resumable = null;
        if (!saved.Matches(f)) return; // другой заказ — отложенный больше не нужен

        double gapKm = _trip!.ResumeFrom(saved, f);
        var note = $"↻ Рейс «{saved.CargoName}» продолжен с сохранённого места: проехано {_trip.DrivenKm:0} км";
        if (gapKm >= 1)
            note += $", из них {gapKm:0} км без трекера (топливо и превышения за них не учтены)";
        Notice?.Invoke(note + ".");
        SaveState(force: true);
    }

    /// <summary>Сохранить текущий рейс сейчас (при закрытии программы).</summary>
    public void SaveNow()
    {
        lock (_lock) SaveState(force: true);
    }

    private void SaveState(bool force, ActiveTripState? state = null)
    {
        if (_statePath == null || (!force && DateTime.Now - _lastSave < TimeSpan.FromSeconds(5))) return;
        state ??= _trip?.Snapshot();
        if (state == null) return;
        _lastSave = DateTime.Now;
        try
        {
            // Через временный файл, чтобы при сбое не остался обрезанный JSON
            var tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, ConfigStore.Json));
            File.Move(tmp, _statePath, overwrite: true);
        }
        catch (IOException) { /* не удалось сохранить — попробуем в следующий раз */ }
        catch (UnauthorizedAccessException) { }
    }

    private void DeleteState()
    {
        _resumable = null;
        if (_statePath == null) return;
        try { File.Delete(_statePath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private ActiveTripState? LoadState()
    {
        if (_statePath == null || !File.Exists(_statePath)) return null;
        try { return JsonSerializer.Deserialize<ActiveTripState>(File.ReadAllText(_statePath), ConfigStore.Json); }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    private TripResult Finish(TelemetryFrame f, TripStatus status)
    {
        var t = _trip!;
        _trip = null;
        DeleteState();

        var d = f.Delivered;
        var result = new TripResult
        {
            Status = status,
            StartedAt = t.StartedAt,
            FinishedAt = DateTime.Now,
            CargoId = t.CargoId,
            CargoName = t.CargoName,
            CargoMassT = t.CargoMassKg / 1000.0,
            Traits = _catalog.Classify(t.CargoId, t.CargoName),
            SpecialJob = t.SpecialJob,
            From = t.From,
            To = t.To,
            FromCityId = t.FromCityId,
            ToCityId = t.ToCityId,
            TruckName = t.TruckName,
            TrailerCount = t.TrailerCount,
            TruckDamage = t.TruckDamage,
            TrailerDamage = t.TrailerDamage,
            DrivenKm = t.DrivenKm,
            FuelUsedL = t.FuelUsedL,
            FinesCount = t.FinesCount,
            FinesTotal = t.FinesTotal,
            TollsTotal = t.TollsTotal,
            SpeedingSeconds = t.SpeedingSeconds,
            MaxSpeedKph = t.MaxSpeedKph,
            StartedMidJob = t.StartedMidJob,
        };

        if (status == TripStatus.Delivered && d != null)
        {
            result.JobDistanceKm = d.DistanceKm;
            result.Revenue = d.Revenue;
            result.EarnedXp = d.EarnedXp;
            result.CargoDamage = d.CargoDamage > 0 ? d.CargoDamage : t.LastCargoDamage;
            result.AutoParked = d.AutoParked;
        }
        else
        {
            result.Revenue = -f.CancelPenalty;
            result.CargoDamage = t.LastCargoDamage;
        }

        _calculator.Apply(result);
        return result;
    }

    private static bool Rising(TelemetryFrame? prev, TelemetryFrame cur, Func<TelemetryFrame, bool> flag)
        => flag(cur) && (prev == null || !flag(prev));
}

/// <summary>Состояние текущего (ещё не завершённого) рейса.</summary>
public sealed class ActiveTrip
{
    public DateTime StartedAt { get; private set; }
    public bool StartedMidJob { get; private set; }

    public string CargoId { get; private set; } = "";
    public string CargoName { get; private set; } = "";
    public float CargoMassKg { get; private set; }
    public bool SpecialJob { get; private set; }
    public string From { get; private set; } = "";
    public string To { get; private set; } = "";
    public string FromCityId { get; private set; } = "";
    public string ToCityId { get; private set; } = "";
    public string TruckName { get; private set; } = "";
    public int TrailerCount { get; private set; }
    public ulong PlannedIncome { get; private set; }
    public uint PlannedDistanceKm { get; private set; }
    public float LastCargoDamage { get; private set; }

    public double DrivenKm { get; private set; }
    public double FuelUsedL { get; private set; }
    public double SpeedingSeconds { get; private set; }
    public double MaxSpeedKph { get; private set; }
    public int FinesCount { get; set; }
    public long FinesTotal { get; set; }
    public long TollsTotal { get; set; }

    // Урон считаем как прирост износа с начала рейса (ремонт по пути урон не «обнуляет»)
    private float? _truckWearStart;
    private float? _trailerWearStart;
    public double TruckDamage { get; private set; }
    public double TrailerDamage { get; private set; }

    // Ход рейса
    public bool CargoLoaded { get; private set; }
    /// <summary>Проехано с грузом (запасной способ оценить прогресс, если навигатор не проложен).</summary>
    public double LoadedKm { get; private set; }
    /// <summary>Осталось по навигатору, км; null — маршрут не проложен.</summary>
    public double? NavRemainingKm { get; private set; }
    public double? NavEtaSec { get; private set; }
    public int? DeadlineMinutesLeft { get; private set; }

    /// <summary>Самый большой остаток по навигатору с момента погрузки.</summary>
    private double _navMaxLoadedKm;

    /// <summary>Последнее показание одометра — чтобы после перезапуска досчитать пробег без трекера.</summary>
    private double _lastOdometerKm;

    /// <summary>Сколько осталось до финиша, км: по навигатору или по плану заказа минус проеханное с грузом.</summary>
    public double RemainingKm => NavRemainingKm ?? Math.Max(0, PlannedDistanceKm - LoadedKm);

    /// <summary>
    /// Полная длина пути с грузом. Дистанция заказа от игры не включает паромы, а навигатор
    /// их считает (Неаполь → Дуррес: 228 км по заказу, 327 км по навигатору), поэтому берём большее
    /// из: дистанции заказа, наибольшего остатка по навигатору и «проехано с грузом + осталось».
    /// </summary>
    public double TotalKm => Math.Max(Math.Max(PlannedDistanceKm, _navMaxLoadedKm), LoadedKm + RemainingKm);

    /// <summary>Доля пройденного пути с грузом, 0..1 (пока едем за грузом — 0).</summary>
    public double Progress
    {
        get
        {
            if (!CargoLoaded || TotalKm <= 0) return 0;
            return Math.Clamp(1 - RemainingKm / TotalKm, 0, 1);
        }
    }

    public static ActiveTrip Begin(TelemetryFrame f, bool midJob)
        => new() { StartedAt = DateTime.Now, StartedMidJob = midJob };

    public ActiveTripState Snapshot() => new()
    {
        SavedAt = DateTime.Now,
        StartedAt = StartedAt, StartedMidJob = StartedMidJob,
        CargoId = CargoId, CargoName = CargoName, CargoMassKg = CargoMassKg, SpecialJob = SpecialJob,
        From = From, To = To, FromCityId = FromCityId, ToCityId = ToCityId,
        TruckName = TruckName, TrailerCount = TrailerCount,
        PlannedIncome = PlannedIncome, PlannedDistanceKm = PlannedDistanceKm, LastCargoDamage = LastCargoDamage,
        DrivenKm = DrivenKm, LoadedKm = LoadedKm, FuelUsedL = FuelUsedL,
        SpeedingSeconds = SpeedingSeconds, MaxSpeedKph = MaxSpeedKph,
        FinesCount = FinesCount, FinesTotal = FinesTotal, TollsTotal = TollsTotal,
        TruckWearStart = _truckWearStart, TrailerWearStart = _trailerWearStart,
        TruckDamage = TruckDamage, TrailerDamage = TrailerDamage,
        NavMaxLoadedKm = _navMaxLoadedKm, LastOdometerKm = _lastOdometerKm,
    };

    /// <summary>
    /// Продолжает отложенный рейс: возвращает накопленное и досчитывает по одометру
    /// километры, проеханные, пока трекер был закрыт. Возвращает эти «лишние» километры.
    /// </summary>
    public double ResumeFrom(ActiveTripState s, TelemetryFrame f)
    {
        double gapKm = f.OdometerKm > 0 && s.LastOdometerKm > 0 ? f.OdometerKm - s.LastOdometerKm : 0;
        if (gapKm is < 0 or > 5000) gapKm = 0; // загрузили более старое сохранение или другой грузовик

        StartedAt = s.StartedAt;
        StartedMidJob = s.StartedMidJob;
        DrivenKm = s.DrivenKm + gapKm;
        LoadedKm = s.LoadedKm + (f.CargoLoaded ? gapKm : 0);
        FuelUsedL = s.FuelUsedL;
        SpeedingSeconds = s.SpeedingSeconds;
        MaxSpeedKph = Math.Max(MaxSpeedKph, s.MaxSpeedKph);
        FinesCount += s.FinesCount;
        FinesTotal += s.FinesTotal;
        TollsTotal += s.TollsTotal;
        TrailerCount = Math.Max(TrailerCount, s.TrailerCount);
        _truckWearStart = s.TruckWearStart ?? _truckWearStart;
        _trailerWearStart = s.TrailerWearStart ?? _trailerWearStart;
        TruckDamage = Math.Max(s.TruckDamage, f.TruckWear - (_truckWearStart ?? f.TruckWear));
        TrailerDamage = f.TrailerCount > 0
            ? Math.Max(s.TrailerDamage, f.TrailerWear - (_trailerWearStart ?? f.TrailerWear))
            : s.TrailerDamage;
        _navMaxLoadedKm = Math.Max(_navMaxLoadedKm, s.NavMaxLoadedKm);
        return gapKm;
    }

    public void Update(TelemetryFrame f, TelemetryFrame? prev, ScoreSettings s)
    {
        // Данные заказа обновляем постоянно: игра иногда заполняет их с задержкой
        if (!string.IsNullOrEmpty(f.CargoId))
        {
            CargoId = f.CargoId;
            CargoName = f.CargoName;
            CargoMassKg = f.CargoMassKg;
            SpecialJob = f.SpecialJob;
            From = Place(f.SourceCity, f.SourceCompany);
            To = Place(f.DestinationCity, f.DestinationCompany);
            FromCityId = f.SourceCityId;
            ToCityId = f.DestinationCityId;
            PlannedIncome = f.PlannedIncome;
            PlannedDistanceKm = f.PlannedDistanceKm;
            LastCargoDamage = f.CargoDamage;
        }
        if (!string.IsNullOrEmpty(f.TruckName)) TruckName = f.TruckName;
        if (f.OdometerKm > 0) _lastOdometerKm = f.OdometerKm;

        CargoLoaded = f.CargoLoaded;
        NavRemainingKm = f.NavDistanceKm > 0.05 ? f.NavDistanceKm : null;
        if (f.CargoLoaded && NavRemainingKm is { } nav) _navMaxLoadedKm = Math.Max(_navMaxLoadedKm, nav);
        NavEtaSec = f.NavTimeSec > 0 ? f.NavTimeSec : null;
        DeadlineMinutesLeft = f.HasDeadline ? f.DeadlineMinutesLeft : null;

        _truckWearStart ??= f.TruckWear;
        TruckDamage = Math.Max(TruckDamage, f.TruckWear - _truckWearStart.Value);
        if (f.TrailerCount > 0)
        {
            TrailerCount = Math.Max(TrailerCount, f.TrailerCount);
            _trailerWearStart ??= f.TrailerWear;
            TrailerDamage = Math.Max(TrailerDamage, f.TrailerWear - _trailerWearStart.Value);
        }

        if (prev == null || f.Paused || prev.Paused) return;

        // Пробег: прирост одометра (скачки > 5 км за кадр — паром/телепорт, не считаем)
        double dKm = f.OdometerKm - prev.OdometerKm;
        if (dKm > 0 && dKm < 5)
        {
            DrivenKm += dKm;
            if (f.CargoLoaded) LoadedKm += dKm;
        }

        // Топливо: считаем только уменьшение (рост — это заправка)
        double dFuel = prev.FuelLiters - f.FuelLiters;
        if (dFuel > 0 && dFuel < 5) FuelUsedL += dFuel;

        MaxSpeedKph = Math.Max(MaxSpeedKph, f.SpeedKph);

        // Превышение: по игровому времени между кадрами
        double dt = (f.TimestampUs - prev.TimestampUs) / 1_000_000.0;
        if (f.TimestampUs > prev.TimestampUs && dt < 2 &&
            f.SpeedLimitKph > 1 && f.SpeedKph > f.SpeedLimitKph + s.SpeedingToleranceKph)
        {
            SpeedingSeconds += dt;
        }
    }

    private static string Place(string city, string company)
        => string.IsNullOrEmpty(company) ? city : $"{city} ({company})";
}

/// <summary>Сохранённое состояние незавершённого рейса (data/active_trip.json).</summary>
public sealed class ActiveTripState
{
    public DateTime SavedAt { get; set; }
    public DateTime StartedAt { get; set; }
    public bool StartedMidJob { get; set; }

    // По этим полям узнаём «тот же заказ»
    public string CargoId { get; set; } = "";
    public string FromCityId { get; set; } = "";
    public string ToCityId { get; set; } = "";
    public ulong PlannedIncome { get; set; }

    public string CargoName { get; set; } = "";
    public float CargoMassKg { get; set; }
    public bool SpecialJob { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string TruckName { get; set; } = "";
    public int TrailerCount { get; set; }
    public uint PlannedDistanceKm { get; set; }
    public float LastCargoDamage { get; set; }

    public double DrivenKm { get; set; }
    public double LoadedKm { get; set; }
    public double FuelUsedL { get; set; }
    public double SpeedingSeconds { get; set; }
    public double MaxSpeedKph { get; set; }
    public int FinesCount { get; set; }
    public long FinesTotal { get; set; }
    public long TollsTotal { get; set; }

    public float? TruckWearStart { get; set; }
    public float? TrailerWearStart { get; set; }
    public double TruckDamage { get; set; }
    public double TrailerDamage { get; set; }
    public double NavMaxLoadedKm { get; set; }
    public double LastOdometerKm { get; set; }

    public bool Matches(TelemetryFrame f)
        => CargoId == f.CargoId && FromCityId == f.SourceCityId && ToCityId == f.DestinationCityId
           && PlannedIncome == f.PlannedIncome;
}
