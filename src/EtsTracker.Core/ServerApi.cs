using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace EtsTracker;

/// <summary>Формат JSON между трекером и сервером: один на обе стороны.</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() },
    };
}

// Данные, которыми обмениваются трекер и сервер компании.
// Трекер присылает только измеренное; очки, зарплату и класс груза считает сервер.

public sealed record RegisterRequest(string Invite, string Nickname, string Password,
    DriverRank? Rank = null, bool PermitAdr = false, bool PermitHeavy = false, bool PermitArticulated = false);

public sealed record LoginRequest(string Nickname, string Password);

public sealed record AuthResponse(string Token, ServerDriver Driver);

/// <summary>Допуски null: их не прислал сервер старой версии.</summary>
public sealed record ServerDriver(long Id, string Nickname, DriverRank Rank, bool IsAdmin,
    bool? PermitAdr = null, bool? PermitHeavy = null, bool? PermitArticulated = null);

public sealed record InviteRequest(DriverRank Rank);

public sealed record InviteResponse(string Code, DriverRank Rank);

public sealed record ApiError(string Error);

/// <summary>Один рейс для отправки. ClientId — чтобы повторная отправка не создала дубль.</summary>
public sealed record TripUpload
{
    public Guid ClientId { get; init; }
    public TripStatus Status { get; init; }
    public TripKind Kind { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public string CargoId { get; init; } = "";
    public string CargoName { get; init; } = "";
    public double CargoMassT { get; init; }
    public bool SpecialJob { get; init; }
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string FromCityId { get; init; } = "";
    public string ToCityId { get; init; } = "";
    public string TruckName { get; init; } = "";
    public int TrailerCount { get; init; }
    public double JobDistanceKm { get; init; }
    public double DrivenKm { get; init; }
    public double FuelUsedL { get; init; }
    public long Revenue { get; init; }
    public int EarnedXp { get; init; }
    public double CargoDamage { get; init; }
    public double TruckDamage { get; init; }
    public double TrailerDamage { get; init; }
    public bool AutoParked { get; init; }
    public int FinesCount { get; init; }
    public long FinesTotal { get; init; }
    public long TollsTotal { get; init; }
    public double SpeedingSeconds { get; init; }
    public double MaxSpeedKph { get; init; }
    public bool StartedMidJob { get; init; }

    public static TripUpload FromTrip(TripResult t) => new()
    {
        ClientId = t.ClientId,
        Status = t.Status,
        Kind = t.Kind,
        StartedAt = t.StartedAt,
        FinishedAt = t.FinishedAt,
        CargoId = t.CargoId,
        CargoName = t.CargoName,
        CargoMassT = t.CargoMassT,
        SpecialJob = t.SpecialJob,
        From = t.From,
        To = t.To,
        FromCityId = t.FromCityId,
        ToCityId = t.ToCityId,
        TruckName = t.TruckName,
        TrailerCount = t.TrailerCount,
        JobDistanceKm = t.JobDistanceKm,
        DrivenKm = t.DrivenKm,
        FuelUsedL = t.FuelUsedL,
        Revenue = t.Revenue,
        EarnedXp = t.EarnedXp,
        CargoDamage = t.CargoDamage,
        TruckDamage = t.TruckDamage,
        TrailerDamage = t.TrailerDamage,
        AutoParked = t.AutoParked,
        FinesCount = t.FinesCount,
        FinesTotal = t.FinesTotal,
        TollsTotal = t.TollsTotal,
        SpeedingSeconds = t.SpeedingSeconds,
        MaxSpeedKph = t.MaxSpeedKph,
        StartedMidJob = t.StartedMidJob,
    };

    /// <summary>Заполняет рейс измеренными значениями; расчётные поля заполняет сервер.</summary>
    public TripResult ToTrip(long driverId) => new()
    {
        ClientId = ClientId,
        DriverId = driverId,
        Status = Status,
        Kind = Kind,
        StartedAt = StartedAt.UtcDateTime,
        FinishedAt = FinishedAt.UtcDateTime,
        CargoId = CargoId,
        CargoName = CargoName,
        CargoMassT = CargoMassT,
        SpecialJob = SpecialJob,
        From = From,
        To = To,
        FromCityId = FromCityId,
        ToCityId = ToCityId,
        TruckName = TruckName,
        TrailerCount = TrailerCount,
        JobDistanceKm = JobDistanceKm,
        DrivenKm = DrivenKm,
        FuelUsedL = FuelUsedL,
        Revenue = Revenue,
        EarnedXp = EarnedXp,
        CargoDamage = CargoDamage,
        TruckDamage = TruckDamage,
        TrailerDamage = TrailerDamage,
        AutoParked = AutoParked,
        FinesCount = FinesCount,
        FinesTotal = FinesTotal,
        TollsTotal = TollsTotal,
        SpeedingSeconds = SpeedingSeconds,
        MaxSpeedKph = MaxSpeedKph,
        StartedMidJob = StartedMidJob,
    };

    /// <summary>Грубая проверка: отсекает явно невозможные значения. Вернёт текст ошибки или null.</summary>
    public string? Validate()
    {
        if (ClientId == Guid.Empty) return "Нет идентификатора рейса.";
        if (FinishedAt < StartedAt) return "Рейс закончился раньше, чем начался.";
        if (FinishedAt > DateTimeOffset.UtcNow.AddDays(1)) return "Рейс из будущего.";
        if (JobDistanceKm is < 0 or > 20_000 || DrivenKm is < 0 or > 20_000) return "Невозможная дистанция.";
        if (CargoMassT is < 0 or > 500) return "Невозможная масса груза.";
        if (FuelUsedL is < 0 or > 20_000) return "Невозможный расход топлива.";
        if (CargoDamage is < 0 or > 1 || TruckDamage is < 0 or > 1 || TrailerDamage is < 0 or > 1) return "Невозможный урон.";
        if (Math.Abs(Revenue) > 10_000_000) return "Невозможный доход.";
        if (FinesCount < 0 || SpeedingSeconds < 0) return "Отрицательные штрафы.";
        return null;
    }
}

public sealed record TripUploadResult(int Accepted, int Duplicates, List<Guid> Saved, List<TripRejected> Rejected);

public sealed record TripRejected(Guid ClientId, string Error);

public sealed record LeaderboardRow
{
    public int Place { get; init; }
    public long DriverId { get; init; }
    public string Nickname { get; init; } = "";
    public DriverRank Rank { get; init; }
    public string RankName { get; init; } = "";
    public bool IsAdmin { get; init; }
    public int Delivered { get; init; }
    public int Convoys { get; init; }
    public double Km { get; init; }
    public double Tonnes { get; init; }
    public long Score { get; init; }
    public double Salary { get; init; }
    /// <summary>Премии, начисленные администратором за период.</summary>
    public double Bonus { get; init; }
    public int Perfect { get; init; }
    public int Fines { get; init; }
    public int Level { get; init; }
    public int Achievements { get; init; }
}

public sealed record Leaderboard
{
    public string CompanyName { get; init; } = "";
    public string Period { get; init; } = "";
    public string Metric { get; init; } = "";
    public int AchievementsTotal { get; init; }
    /// <summary>Общий счёт компании за всё время, в любом периоде один и тот же.</summary>
    public double CompanyAccount { get; init; }
    public double TotalKm { get; init; }
    public double TotalTonnes { get; init; }
    public double TotalSalary { get; init; }
    public double TotalBonus { get; init; }
    public int TotalDelivered { get; init; }
    public List<LeaderboardRow> Rows { get; init; } = new();
}

/// <summary>Достижение водителя в рейтинге.</summary>
public sealed record AchievementInfo(
    string Id, string Icon, string Title, string Description,
    bool Unlocked, DateTime? UnlockedAt, double Current, double Target, string Unit);

public sealed record DriverAchievements(
    long DriverId, string Nickname, int Unlocked, int Total, List<AchievementInfo> Items);

/// <summary>Что трекер сообщает о текущем рейсе раз в несколько секунд.</summary>
public sealed record LiveTripUpdate
{
    public string CargoName { get; init; } = "";
    public double CargoMassT { get; init; }
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public bool CargoLoaded { get; init; }
    public double Progress { get; init; }
    public double RemainingKm { get; init; }
    public double SpeedKph { get; init; }
    public bool Paused { get; init; }
}

/// <summary>Водитель, который сейчас в рейсе.</summary>
public sealed record LiveTrip(long DriverId, string Nickname, string RankName, LiveTripUpdate Trip, DateTime UpdatedAt);

/// <summary>Премия, которую администратор начисляет водителю.</summary>
public sealed record BonusRequest(long DriverId, double Amount, string Reason);

public sealed record BonusEntry(long Id, long DriverId, double Amount, string Reason, DateTime CreatedAt, string CreatedBy);

public enum TripApproval
{
    Pending,    // ждёт проверки
    Approved,   // принят, идёт в рейтинг
    Rejected,   // отклонён
}

/// <summary>Рейс на сервере — для проверки администратором и для самого водителя.</summary>
public sealed record ServerTrip
{
    public long Id { get; init; }
    public long DriverId { get; init; }
    public string Nickname { get; init; } = "";
    public TripApproval Approval { get; init; }
    public TripStatus Status { get; init; }
    public TripKind Kind { get; init; }
    public CargoClass CargoClass { get; init; }
    public DateTime FinishedAt { get; init; }
    public string CargoName { get; init; } = "";
    public double CargoMassT { get; init; }
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public double DistanceKm { get; init; }
    public double CargoDamage { get; init; }
    public int FinesCount { get; init; }
    public bool StartedMidJob { get; init; }
    public int Score { get; init; }
    public string Grade { get; init; } = "";
    public double Salary { get; init; }
    public string ReviewNote { get; init; } = "";
    public string ReviewedBy { get; init; } = "";
}

/// <summary>Решение по рейсу. Тип поездки и класс груза можно поправить: «Важный» и «Срочный» игра не сообщает.</summary>
public sealed record TripReview(bool Approve, TripKind? Kind = null, CargoClass? CargoClass = null, string? Note = null);

public sealed record ServerDriverInfo(
    long Id, string Nickname, DriverRank Rank, string RankName,
    bool PermitAdr, bool PermitHeavy, bool PermitArticulated, bool IsAdmin);

public sealed record DriverUpdate(DriverRank Rank, bool PermitAdr, bool PermitHeavy, bool PermitArticulated, bool IsAdmin);
