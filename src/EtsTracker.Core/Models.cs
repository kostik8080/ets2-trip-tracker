namespace EtsTracker;

/// <summary>
/// Один «кадр» телеметрии в удобном виде. Отвязывает логику трекера
/// от библиотеки SDK, поэтому трекер можно проверять без запущенной игры (--demo).
/// </summary>
public sealed record TelemetryFrame
{
    public bool SdkActive { get; init; }
    public bool Paused { get; init; }
    /// <summary>Игровое время в микросекундах.</summary>
    public ulong TimestampUs { get; init; }
    /// <summary>Игровые часы: минуты от начала игры (день недели и время суток в игре).</summary>
    public uint GameTimeMinutes { get; init; }

    // Флаги событий (поднимаются игрой на короткое время)
    public bool OnJob { get; init; }
    public bool JobDeliveredFlag { get; init; }
    public bool JobCancelledFlag { get; init; }
    public bool FinedFlag { get; init; }
    public bool TollgateFlag { get; init; }

    // Заказ
    public string CargoId { get; init; } = "";
    public string CargoName { get; init; } = "";
    public float CargoMassKg { get; init; }
    public float CargoDamage { get; init; }          // 0..1
    public bool SpecialJob { get; init; }
    public string SourceCity { get; init; } = "";
    public string SourceCityId { get; init; } = "";
    public string SourceCompany { get; init; } = "";
    public string DestinationCity { get; init; } = "";
    public string DestinationCityId { get; init; } = "";
    public string DestinationCompany { get; init; } = "";
    public ulong PlannedIncome { get; init; }
    public uint PlannedDistanceKm { get; init; }
    /// <summary>Груз прицеплен (до этого водитель едет за грузом).</summary>
    public bool CargoLoaded { get; init; }
    /// <summary>Есть ли у заказа срок доставки.</summary>
    public bool HasDeadline { get; init; }
    /// <summary>Игровых минут до срока доставки; меньше нуля — опоздание.</summary>
    public int DeadlineMinutesLeft { get; init; }

    // Навигатор (маршрут в Route Advisor); 0 — маршрут не проложен
    public float NavDistanceKm { get; init; }
    public float NavTimeSec { get; init; }

    // Грузовик
    public string TruckName { get; init; } = "";
    public float OdometerKm { get; init; }
    public float FuelLiters { get; init; }
    public float SpeedKph { get; init; }
    public float SpeedLimitKph { get; init; }
    /// <summary>Износ тягача 0..1 — самая изношенная из частей (двигатель, КПП, кабина, шасси, колёса).</summary>
    public float TruckWear { get; init; }

    // Прицепы
    public int TrailerCount { get; init; }
    /// <summary>Износ прицепа 0..1 — максимум по прицепленным прицепам.</summary>
    public float TrailerWear { get; init; }

    // Данные событий
    public DeliveredInfo? Delivered { get; init; }
    public long CancelPenalty { get; init; }
    public long FineAmount { get; init; }
    public string FineOffence { get; init; } = "";
    public long TollAmount { get; init; }
}

public sealed record DeliveredInfo(
    long Revenue,
    float DistanceKm,
    float CargoDamage,
    int EarnedXp,
    bool AutoParked,
    bool AutoLoaded);

public enum TripStatus { Delivered, Cancelled }

[Flags]
public enum CargoTraits
{
    None = 0,
    Fragile = 1,     // хрупкий
    Valuable = 2,    // ценный
    Dangerous = 4,   // опасный (ADR)
}

/// <summary>Строка расшифровки очков: «за что сколько начислено».</summary>
public sealed record ScoreLine(string Label, double Points);

/// <summary>Итог завершённого рейса — то, что сохраняется в базу.</summary>
public sealed class TripResult
{
    public long Id { get; set; }
    /// <summary>Постоянный идентификатор рейса: по нему сервер отличает повторную отправку от нового рейса.</summary>
    public Guid ClientId { get; set; }
    /// <summary>Демо-рейс: на сервер не отправляется.</summary>
    public bool IsDemo { get; set; }
    public long? DriverId { get; set; }
    public TripStatus Status { get; set; }
    public TripKind Kind { get; set; }
    public CargoClass CargoClass { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }

    public string CargoId { get; set; } = "";
    public string CargoName { get; set; } = "";
    public double CargoMassT { get; set; }
    public CargoTraits Traits { get; set; }
    public bool SpecialJob { get; set; }

    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string FromCityId { get; set; } = "";
    public string ToCityId { get; set; } = "";
    public string TruckName { get; set; } = "";
    public int TrailerCount { get; set; }

    /// <summary>Дистанция заказа по данным игры (если игра её не дала — по одометру).</summary>
    public double DistanceKm => JobDistanceKm > 0 ? JobDistanceKm : DrivenKm;
    /// <summary>Дистанция заказа по данным игры.</summary>
    public double JobDistanceKm { get; set; }
    /// <summary>Реально проехано по одометру (включая порожний перегон к грузу).</summary>
    public double DrivenKm { get; set; }
    public double FuelUsedL { get; set; }
    public double FuelPer100Km => DrivenKm > 1 ? FuelUsedL / DrivenKm * 100 : 0;

    public long Revenue { get; set; }
    public int EarnedXp { get; set; }
    public double CargoDamage { get; set; }          // 0..1
    public double TruckDamage { get; set; }          // 0..1, прирост износа за рейс
    public double TrailerDamage { get; set; }        // 0..1, прирост износа за рейс
    public bool AutoParked { get; set; }

    public int FinesCount { get; set; }
    public long FinesTotal { get; set; }
    public long TollsTotal { get; set; }
    public double SpeedingSeconds { get; set; }
    public double MaxSpeedKph { get; set; }
    public bool StartedMidJob { get; set; }

    public int Score { get; set; }
    public string Grade { get; set; } = "";
    public List<ScoreLine> Breakdown { get; set; } = new();

    /// <summary>Зарплата по ставкам компании, €.</summary>
    public double Salary { get; set; }
    public List<ScoreLine> SalaryBreakdown { get; set; } = new();
}
