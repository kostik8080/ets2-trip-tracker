namespace EtsTracker;

/// <summary>
/// Следит за телеметрией: ловит начало и конец заказа, копит пробег,
/// расход топлива, штрафы, превышения. По завершении отдаёт TripResult.
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

    public event Action<ActiveTrip>? TripStarted;
    public event Action<TripResult>? TripFinished;
    public event Action<string>? Notice;

    public TelemetryFrame? LastFrame { get; private set; }
    public DateTime LastFrameAt { get; private set; }
    public ActiveTrip? Current { get { lock (_lock) return _trip; } }

    public TripTracker(CargoCatalog catalog, ScoreSettings settings)
    {
        _catalog = catalog;
        _settings = settings;
        _calculator = new ScoreCalculator(settings);
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

                if (Rising(prev, f, x => x.FinedFlag))
                {
                    _trip.FinesCount++;
                    _trip.FinesTotal += f.FineAmount;
                    Notice?.Invoke($"🚨 Штраф: {f.FineOffence}, €{f.FineAmount}");
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
                        _trip = null;
                        Notice?.Invoke("ℹ Заказ пропал без доставки (смена сохранения?) — рейс не засчитан.");
                    }
                }
                else _offJobSince = null;
            }
            if (_trip == null) _offJobSince = null;
        }

        // События вызываем вне блокировки
        if (started != null) TripStarted?.Invoke(started);
        if (finished != null) TripFinished?.Invoke(finished);
    }

    private TripResult Finish(TelemetryFrame f, TripStatus status)
    {
        var t = _trip!;
        _trip = null;

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
            TruckName = t.TruckName,
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
    public DateTime StartedAt { get; private init; }
    public bool StartedMidJob { get; private init; }

    public string CargoId { get; private set; } = "";
    public string CargoName { get; private set; } = "";
    public float CargoMassKg { get; private set; }
    public bool SpecialJob { get; private set; }
    public string From { get; private set; } = "";
    public string To { get; private set; } = "";
    public string TruckName { get; private set; } = "";
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

    public static ActiveTrip Begin(TelemetryFrame f, bool midJob)
        => new() { StartedAt = DateTime.Now, StartedMidJob = midJob };

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
            PlannedIncome = f.PlannedIncome;
            PlannedDistanceKm = f.PlannedDistanceKm;
            LastCargoDamage = f.CargoDamage;
        }
        if (!string.IsNullOrEmpty(f.TruckName)) TruckName = f.TruckName;

        if (prev == null || f.Paused || prev.Paused) return;

        // Пробег: прирост одометра (скачки > 5 км за кадр — паром/телепорт, не считаем)
        double dKm = f.OdometerKm - prev.OdometerKm;
        if (dKm > 0 && dKm < 5) DrivenKm += dKm;

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
