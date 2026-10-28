namespace EtsTracker;

/// <summary>Звание водителя в компании (лист «Стоимость», колонка «Ранг»).</summary>
public enum DriverRank
{
    Trainee,      // Стажер
    Junior,       // Младший водитель
    Class3,       // Водитель 3 класса
    Class2,       // Водитель 2 класса
    Class1,       // Водитель 1 класса
    Pro,          // Водитель-Профи
    Instructor,   // Инструктор
    Honored,      // Почетный водитель
}

/// <summary>Класс груза (лист «Стоимость», колонка «Класс груза»).</summary>
public enum CargoClass
{
    Normal,       // Обычный
    Fragile,      // Хрупкий
    Important,    // Важный
    Expensive,    // Дорогостоящий
    Urgent,       // Срочный
    Adr,          // ADR
    Heavy,        // Тяжелый
    HeavyHaul,    // Хави карго (спецтранспорт)
    Articulated,  // Сочлененный (несколько прицепов)
}

public enum TripKind
{
    Solo,         // Одиночная поездка
    Convoy,       // Конвой
}

/// <summary>Водитель компании (лист «Сотрудники»).</summary>
public sealed class Driver
{
    public long Id { get; set; }
    public string Nickname { get; set; } = "";
    /// <summary>Регистрационный номер машины в компании: 01, 02…</summary>
    public string RegNumber { get; set; } = "";
    public string Truck { get; set; } = "";
    public DriverRank Rank { get; set; }
    // Допуски
    public bool PermitAdr { get; set; }
    public bool PermitHeavy { get; set; }
    public bool PermitArticulated { get; set; }
    public DateTime CreatedAt { get; set; }

    public override string ToString() => Nickname;
}

public static class Names
{
    public static string Of(DriverRank r) => r switch
    {
        DriverRank.Trainee => "Стажер",
        DriverRank.Junior => "Младший водитель",
        DriverRank.Class3 => "Водитель 3 класса",
        DriverRank.Class2 => "Водитель 2 класса",
        DriverRank.Class1 => "Водитель 1 класса",
        DriverRank.Pro => "Водитель-Профи",
        DriverRank.Instructor => "Инструктор",
        DriverRank.Honored => "Почетный водитель",
        _ => r.ToString(),
    };

    public static string Of(CargoClass c) => c switch
    {
        CargoClass.Normal => "Обычный",
        CargoClass.Fragile => "Хрупкий",
        CargoClass.Important => "Важный",
        CargoClass.Expensive => "Дорогостоящий",
        CargoClass.Urgent => "Срочный",
        CargoClass.Adr => "ADR",
        CargoClass.Heavy => "Тяжелый",
        CargoClass.HeavyHaul => "Хави карго",
        CargoClass.Articulated => "Сочлененный",
        _ => c.ToString(),
    };

    public static string Of(TripKind k) => k == TripKind.Convoy ? "Конвой" : "Одиночная";
}

public sealed class DistanceRate
{
    /// <summary>Граница дальности рейса, км; null — «более».</summary>
    public double? UpToKm { get; set; }
    public double RatePerKm { get; set; }
}

public sealed class RankRequirement
{
    public DriverRank Rank { get; set; }
    public int Convoys { get; set; }
    /// <summary>Альтернативное условие, текстом: «или 3 совместных».</summary>
    public string Alternative { get; set; } = "";
}

/// <summary>
/// Ставки компании (лист «Стоимость»). Файл data/company.json —
/// меняйте цифры там, перекомпилировать ничего не нужно.
/// </summary>
public sealed class CompanySettings
{
    /// <summary>Название компании в шапке окна. Меняется в окне «Компания».</summary>
    public string CompanyName { get; set; } = "ВТК-ПЭК";

    /// <summary>Файл логотипа в папке data; пусто — встроенный логотип.</summary>
    public string LogoFile { get; set; } = "";

    /// <summary>€ за 1 км с грузом, зависит от дальности рейса.</summary>
    public List<DistanceRate> DistanceRates { get; set; } = new()
    {
        new() { UpToKm = 1000, RatePerKm = 1.7 },
        new() { UpToKm = 1500, RatePerKm = 1.9 },
        new() { UpToKm = 2500, RatePerKm = 2.1 },
        new() { UpToKm = null, RatePerKm = 2.7 },
    };

    /// <summary>Надбавка за класс груза по рангу, €. Нет класса в списке ранга — груз не по допуску.</summary>
    public Dictionary<DriverRank, Dictionary<CargoClass, double>> ClassBonus { get; set; } = new()
    {
        [DriverRank.Trainee] = new() { [CargoClass.Normal] = 0 },
        [DriverRank.Junior] = new() { [CargoClass.Normal] = 35 },
        [DriverRank.Class3] = new() { [CargoClass.Normal] = 37, [CargoClass.Fragile] = 39 },
        [DriverRank.Class2] = new()
        {
            [CargoClass.Normal] = 42, [CargoClass.Fragile] = 44, [CargoClass.Important] = 43,
            [CargoClass.Expensive] = 45, [CargoClass.Urgent] = 44, [CargoClass.Adr] = 47,
        },
        [DriverRank.Class1] = new()
        {
            [CargoClass.Important] = 53, [CargoClass.Expensive] = 55, [CargoClass.Normal] = 52,
            [CargoClass.Urgent] = 54, [CargoClass.Heavy] = 56, [CargoClass.Adr] = 55, [CargoClass.Fragile] = 54,
        },
        [DriverRank.Pro] = new()
        {
            [CargoClass.Important] = 63, [CargoClass.Expensive] = 65, [CargoClass.Normal] = 62,
            [CargoClass.Urgent] = 64, [CargoClass.Heavy] = 66, [CargoClass.Adr] = 67, [CargoClass.Fragile] = 64,
            [CargoClass.HeavyHaul] = 69, [CargoClass.Articulated] = 69,
        },
        [DriverRank.Instructor] = new()
        {
            [CargoClass.Important] = 73, [CargoClass.Expensive] = 75, [CargoClass.Normal] = 72,
            [CargoClass.Urgent] = 74, [CargoClass.Heavy] = 76, [CargoClass.Adr] = 77, [CargoClass.Fragile] = 74,
            [CargoClass.HeavyHaul] = 79, [CargoClass.Articulated] = 79,
        },
        [DriverRank.Honored] = new()
        {
            [CargoClass.Important] = 83, [CargoClass.Expensive] = 85, [CargoClass.Normal] = 82,
            [CargoClass.Urgent] = 84, [CargoClass.Heavy] = 86, [CargoClass.Adr] = 87, [CargoClass.Fragile] = 84,
            [CargoClass.HeavyHaul] = 89, [CargoClass.Articulated] = 89,
        },
    };

    /// <summary>Ограничение массы груза для младших рангов («Обычный до 20 тонн»).</summary>
    public Dictionary<DriverRank, double> RankMassLimitTonnes { get; set; } = new()
    {
        [DriverRank.Trainee] = 20,
        [DriverRank.Junior] = 20,
    };

    /// <summary>Груз тяжелее — класс «Тяжелый» (если не подходит более «важный» класс).</summary>
    public double HeavyCargoTonnes { get; set; } = 30;

    /// <summary>Города гаражей компании (ID города в игре или название). Рейс в/из них — надбавка.</summary>
    public List<string> GarageCities { get; set; } = new()
    {
        "lublin", "roma", "liverpool", "la_rochelle", "goteborg",
    };
    public double GarageCityBonusPercent { get; set; } = 0.15;

    /// <summary>«За перевозку более 4 грузов за сутки — 4% стоимости груза».</summary>
    public int ManyTripsPerDay { get; set; } = 4;
    public double ManyTripsBonusPercent { get; set; } = 0.04;

    /// <summary>Условия повышения (лист «Стоимость», «Для класса»).</summary>
    public List<RankRequirement> RankRequirements { get; set; } = new()
    {
        new() { Rank = DriverRank.Junior, Convoys = 5, Alternative = "или 3 совместных" },
        new() { Rank = DriverRank.Class3, Convoys = 10, Alternative = "или 4 совместных" },
        new() { Rank = DriverRank.Class2, Convoys = 20, Alternative = "или 5 совместных" },
        new() { Rank = DriverRank.Class1, Convoys = 30, Alternative = "или 6 совместных" },
        new() { Rank = DriverRank.Pro, Convoys = 40, Alternative = "или 7 совместных" },
    };

    /// <summary>Грузовики из автосалона — подсказки в профиле водителя.</summary>
    public List<string> Trucks { get; set; } = new()
    {
        "DAF XF 105", "DAF XF Euro 6", "MAN TGX", "Mercedes New Actros",
        "Renault Premium", "Renault Magnum", "Renault T", "Renault T High",
        "Scania Streamline", "Scania R", "Scania R High", "Scania S", "Scania S High",
        "Volvo FH16 Classic", "Volvo FH Sleeper",
    };

    public double RateFor(double km)
    {
        foreach (var r in DistanceRates.OrderBy(r => r.UpToKm ?? double.MaxValue))
            if (r.UpToKm == null || km <= r.UpToKm) return r.RatePerKm;
        return DistanceRates.LastOrDefault()?.RatePerKm ?? 0;
    }

    /// <summary>Угадывает класс груза по данным игры. «Важный» и «Срочный» игра не сообщает — их ставят вручную.</summary>
    public CargoClass DetectClass(TripResult t)
    {
        if (t.TrailerCount > 1) return CargoClass.Articulated;
        if (t.SpecialJob) return CargoClass.HeavyHaul;
        if (t.Traits.HasFlag(CargoTraits.Dangerous)) return CargoClass.Adr;
        if (t.CargoMassT >= HeavyCargoTonnes) return CargoClass.Heavy;
        if (t.Traits.HasFlag(CargoTraits.Valuable)) return CargoClass.Expensive;
        if (t.Traits.HasFlag(CargoTraits.Fragile)) return CargoClass.Fragile;
        return CargoClass.Normal;
    }

    public bool IsGarageCity(string cityId, string place)
        => GarageCities.Any(g =>
            g.Equals(cityId, StringComparison.OrdinalIgnoreCase) ||
            place.StartsWith(g, StringComparison.OrdinalIgnoreCase));

    public RankRequirement? NextRequirement(DriverRank rank)
        => RankRequirements.Where(r => r.Rank > rank).OrderBy(r => r.Rank).FirstOrDefault();
}

/// <summary>
/// Зарплата за рейс по ставкам компании:
///
///   зарплата = стоимость груза (доход в игре)
///            + км × ставка за км (по дальности рейса)
///            + надбавка за класс груза (по рангу; для ADR, тяжёлых и сочленённых — при наличии допуска)
///            + 15% стоимости груза, если рейс в город гаража или из него
///            + 4% стоимости груза за 5-й и следующие рейсы за сутки
/// </summary>
public sealed class PayCalculator
{
    private readonly CompanySettings _c;

    public PayCalculator(CompanySettings company) => _c = company;

    /// <param name="tripsEarlierToday">Сколько доставленных рейсов у водителя уже было в этот день.</param>
    public void Apply(TripResult trip, Driver driver, int tripsEarlierToday)
    {
        var rank = driver.Rank;
        var lines = new List<ScoreLine>();
        if (trip.Status != TripStatus.Delivered)
        {
            trip.Salary = 0;
            trip.SalaryBreakdown = lines;
            return;
        }

        double cargoValue = Math.Max(0, trip.Revenue);
        lines.Add(new("Стоимость груза", cargoValue));

        double km = trip.DistanceKm;
        double rate = _c.RateFor(km);
        lines.Add(new($"{km:0} км × €{rate:0.0#}", km * rate));

        if (_c.ClassBonus.TryGetValue(rank, out var bonuses) && bonuses.TryGetValue(trip.CargoClass, out var bonus))
        {
            if (MissingPermit(driver, trip.CargoClass) is { } permit)
                lines.Add(new($"Надбавка: нет допуска «{permit}»", 0));
            else if (_c.RankMassLimitTonnes.TryGetValue(rank, out var limit) && trip.CargoMassT > limit)
                lines.Add(new($"Надбавка: груз тяжелее {limit:0} т — не положена", 0));
            else
                lines.Add(new($"Надбавка: {Names.Of(trip.CargoClass)}, {Names.Of(rank)}", bonus));
        }
        else
        {
            lines.Add(new($"Надбавка: класс «{Names.Of(trip.CargoClass)}» не по рангу", 0));
        }

        if (_c.IsGarageCity(trip.FromCityId, trip.From) || _c.IsGarageCity(trip.ToCityId, trip.To))
            lines.Add(new($"Город гаража (+{_c.GarageCityBonusPercent:P0} груза)", cargoValue * _c.GarageCityBonusPercent));

        if (tripsEarlierToday >= _c.ManyTripsPerDay)
            lines.Add(new($"Больше {_c.ManyTripsPerDay} грузов за сутки (+{_c.ManyTripsBonusPercent:P0} груза)",
                cargoValue * _c.ManyTripsBonusPercent));

        trip.SalaryBreakdown = lines;
        trip.Salary = Math.Round(lines.Sum(l => l.Points), 2);
    }

    /// <summary>Какого допуска не хватает водителю для этого класса груза (null — всё в порядке).</summary>
    public static string? MissingPermit(Driver d, CargoClass c) => c switch
    {
        CargoClass.Adr when !d.PermitAdr => "ADR",
        CargoClass.Heavy or CargoClass.HeavyHaul when !d.PermitHeavy => "Тяжёлые",
        CargoClass.Articulated when !d.PermitArticulated => "Сочленённые",
        _ => null,
    };
}
