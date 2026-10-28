namespace EtsTracker;

/// <summary>
/// Записывает завершённые рейсы водителю: класс груза, тип поездки, зарплата.
/// Общий для консоли и окна.
/// </summary>
public sealed class CareerService
{
    private readonly TripRepository _repo;
    private readonly PayCalculator _pay;

    public CompanySettings Company { get; }

    public CareerService(TripRepository repo, CompanySettings company)
    {
        _repo = repo;
        Company = company;
        _pay = new PayCalculator(company);
    }

    public void Record(TripResult trip, Driver? driver, TripKind kind)
    {
        trip.DriverId = driver?.Id;
        trip.Kind = kind;
        trip.CargoClass = Company.DetectClass(trip);
        ApplyPay(trip, driver);
        _repo.Save(trip);
    }

    /// <summary>Ручная поправка типа поездки и класса груза с пересчётом зарплаты.</summary>
    public void Reclassify(TripResult trip, Driver? driver, TripKind kind, CargoClass cargoClass)
    {
        trip.Kind = kind;
        trip.CargoClass = cargoClass;
        ApplyPay(trip, driver);
        _repo.UpdateClassification(trip);
    }

    /// <summary>
    /// Начисляет зарплату рейсам, записанным до регистрации водителя
    /// (у них ещё нет расшифровки зарплаты). Класс груза при этом определяется заново.
    /// </summary>
    public void PayUnpaidTrips(Driver driver)
    {
        foreach (var trip in _repo.GetTrips(driver.Id).Where(t => t.SalaryBreakdown.Count == 0).OrderBy(t => t.FinishedAt))
            Reclassify(trip, driver, trip.Kind, Company.DetectClass(trip));
    }

    private void ApplyPay(TripResult trip, Driver? driver)
    {
        if (driver == null)
        {
            trip.Salary = 0;
            trip.SalaryBreakdown = new() { new("Водитель не выбран — зарплата не начислена", 0) };
            return;
        }
        int earlier = _repo.CountDeliveredSameDayBefore(driver.Id, trip.FinishedAt, trip.Id);
        _pay.Apply(trip, driver, earlier);
    }
}

/// <summary>Уровень водителя по очкам трекера: уровень = ⌊√(очки / 100)⌋ + 1.</summary>
public readonly record struct DriverLevel(int Level, long Score, long LevelStart, long NextLevelAt)
{
    public static DriverLevel From(long score)
    {
        score = Math.Max(0, score);
        int level = (int)Math.Floor(Math.Sqrt(score / 100.0)) + 1;
        return new(level, score, (long)(level - 1) * (level - 1) * 100, (long)level * level * 100);
    }

    /// <summary>Доля пути до следующего уровня, 0..1.</summary>
    public double Progress => NextLevelAt > LevelStart ? (double)(Score - LevelStart) / (NextLevelAt - LevelStart) : 0;
}

/// <summary>Сводка по рейсам за период — как колонки листа «Сотрудники».</summary>
public sealed record DriverStats
{
    public int Delivered { get; init; }
    public int Cancelled { get; init; }
    public int Convoys { get; init; }
    public int SoloTrips { get; init; }
    public double Tonnes { get; init; }
    public double Km { get; init; }
    public double FuelL { get; init; }
    public double Salary { get; init; }
    public long Revenue { get; init; }
    public long Score { get; init; }
    public int Fines { get; init; }
    public int Perfect { get; init; }
    public double AvgMassT { get; init; }
    public double AvgKm { get; init; }
    public double AvgFuelPer100 { get; init; }
    public double AvgTruckDamage { get; init; }
    public double AvgCargoDamage { get; init; }
    public double AvgTrailerDamage { get; init; }

    public static DriverStats Compute(IEnumerable<TripResult> trips)
    {
        var all = trips.ToList();
        var ok = all.Where(t => t.Status == TripStatus.Delivered).ToList();
        double km = ok.Sum(t => t.DistanceKm);
        double driven = ok.Sum(t => t.DrivenKm);
        double fuel = ok.Sum(t => t.FuelUsedL);
        return new DriverStats
        {
            Delivered = ok.Count,
            Cancelled = all.Count - ok.Count,
            Convoys = ok.Count(t => t.Kind == TripKind.Convoy),
            SoloTrips = ok.Count(t => t.Kind == TripKind.Solo),
            Tonnes = ok.Sum(t => t.CargoMassT),
            Km = km,
            FuelL = fuel,
            Salary = ok.Sum(t => t.Salary),
            Revenue = ok.Sum(t => t.Revenue),
            Score = all.Sum(t => (long)t.Score),
            Fines = all.Sum(t => t.FinesCount),
            Perfect = ok.Count(t => t.CargoDamage < 0.005),
            AvgMassT = ok.Count > 0 ? ok.Average(t => t.CargoMassT) : 0,
            AvgKm = ok.Count > 0 ? km / ok.Count : 0,
            AvgFuelPer100 = driven > 1 ? fuel / driven * 100 : 0,
            AvgTruckDamage = ok.Count > 0 ? ok.Average(t => t.TruckDamage) : 0,
            AvgCargoDamage = ok.Count > 0 ? ok.Average(t => t.CargoDamage) : 0,
            AvgTrailerDamage = ok.Count > 0 ? ok.Average(t => t.TrailerDamage) : 0,
        };
    }
}

/// <summary>Что запомнить между запусками: последний выбранный водитель (data/app.json).</summary>
public sealed class AppState
{
    public long? LastDriverId { get; set; }

    public static string PathIn(string dataDir) => Path.Combine(dataDir, "app.json");

    public void Save(string dataDir) => ConfigStore.Save(PathIn(dataDir), this);
}
