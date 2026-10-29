namespace EtsTracker;

/// <summary>Описание достижения: что нужно сделать и как считать прогресс.</summary>
public sealed record AchievementDef(
    string Id, string Icon, string Title, string Description, double Target, string Unit,
    Func<AchievementCounters, double> Value);

/// <summary>Состояние достижения у водителя.</summary>
public sealed record AchievementStatus(AchievementDef Def, double Current, DateTime? UnlockedAt, long? UnlockedTripId)
{
    public bool Unlocked => UnlockedAt != null;
    public double Progress => Unlocked ? 1 : Math.Clamp(Current / Def.Target, 0, 1);
}

/// <summary>Счётчики, которые копятся по рейсам водителя в порядке времени.</summary>
public sealed class AchievementCounters
{
    public int Delivered;
    public double TotalKm;
    public int Perfect;
    public int PerfectStreak;
    /// <summary>Километры подряд без штрафов (штраф обнуляет).</summary>
    public double KmNoFines;
    /// <summary>Километры подряд без превышений скорости.</summary>
    public double KmNoSpeeding;
    public int Adr;
    public int FragilePerfect;
    public int Heavy;
    public int Special;
    public int Articulated;
    public double LongestKm;
    public int Convoys;
    public int GradeS;
    public int Eco;
    public double Salary;
}

/// <summary>
/// Достижения считаются по истории рейсов, а не хранятся отдельно: их нельзя «потерять»,
/// и старые рейсы засчитываются. Дата получения — дата рейса, которым достижение открылось.
/// </summary>
public static class Achievements
{
    public static IReadOnlyList<AchievementDef> All { get; } = new List<AchievementDef>
    {
        // Рейсы
        new("first_trip", "🚚", "Первый рейс", "Доставьте первый груз.", 1, "рейс", c => c.Delivered),
        new("trips_10", "📦", "Десятка", "Доставьте 10 грузов.", 10, "рейсов", c => c.Delivered),
        new("trips_50", "🏅", "Полсотни", "Доставьте 50 грузов.", 50, "рейсов", c => c.Delivered),
        new("trips_100", "🏆", "Сотня", "Доставьте 100 грузов.", 100, "рейсов", c => c.Delivered),

        // Пробег
        new("km_1000", "🛣", "Первая тысяча", "Проедьте 1 000 км с грузом.", 1_000, "км", c => c.TotalKm),
        new("km_10000", "🌍", "Десять тысяч", "Проедьте 10 000 км с грузом.", 10_000, "км", c => c.TotalKm),
        new("km_100000", "🚀", "Сто тысяч", "Проедьте 100 000 км с грузом.", 100_000, "км", c => c.TotalKm),
        new("long_1500", "🧭", "Дальнобой", "Один рейс длиной 1 500 км и больше.", 1_500, "км", c => c.LongestKm),

        // Аккуратность
        new("perfect_1", "✨", "Ни царапины", "Доставьте груз без повреждений (урон меньше 0,5 %).", 1, "рейс", c => c.Perfect),
        new("perfect_10", "💎", "Аккуратист", "10 рейсов без царапин.", 10, "рейсов", c => c.Perfect),
        new("perfect_streak_10", "🧤", "Хрустальные руки", "10 рейсов без царапин подряд.", 10, "подряд", c => c.PerfectStreak),
        new("fragile_5", "🥚", "Бережно", "5 хрупких грузов без царапин.", 5, "рейсов", c => c.FragilePerfect),

        // Дисциплина
        new("nofines_1000", "👮", "1000 км без штрафов", "Проедьте 1 000 км подряд без единого штрафа.", 1_000, "км", c => c.KmNoFines),
        new("nofines_5000", "⚖", "Образцовый водитель", "5 000 км подряд без штрафов.", 5_000, "км", c => c.KmNoFines),
        new("nospeed_1000", "🐢", "Скоростной режим", "1 000 км подряд без превышений скорости.", 1_000, "км", c => c.KmNoSpeeding),
        new("grade_s_5", "⭐", "Отличник", "5 рейсов с оценкой S.", 5, "рейсов", c => c.GradeS),
        new("eco_5", "🍃", "Экономист", "5 рейсов от 300 км с расходом меньше 25 л/100 км.", 5, "рейсов", c => c.Eco),

        // Грузы
        new("adr_1", "☢", "Первый опасный груз", "Доставьте груз класса ADR.", 1, "рейс", c => c.Adr),
        new("adr_10", "🧪", "Химик", "Доставьте 10 опасных грузов.", 10, "рейсов", c => c.Adr),
        new("heavy_1", "🏋", "Тяжеловес", "Груз от 30 т или тяжёлого класса.", 1, "рейс", c => c.Heavy),
        new("special_1", "🏗", "Негабарит", "Рейс спецтранспорта.", 1, "рейс", c => c.Special),
        new("articulated_1", "🔗", "Сцепка", "Груз с двумя прицепами и больше.", 1, "рейс", c => c.Articulated),

        // Компания
        new("convoy_1", "🚛", "В колонне", "Первый конвой.", 1, "конвой", c => c.Convoys),
        new("convoy_10", "🎖", "Конвойщик", "10 конвоев.", 10, "конвоев", c => c.Convoys),
        new("salary_100k", "💶", "Первые сто тысяч", "Заработайте €100 000 зарплаты ВТК.", 100_000, "€", c => c.Salary),
        new("salary_1m", "💰", "Миллионер", "Заработайте €1 000 000 зарплаты ВТК.", 1_000_000, "€", c => c.Salary),
    };

    /// <summary>Проходит по рейсам водителя по порядку и отмечает, каким рейсом открылось каждое достижение.</summary>
    public static List<AchievementStatus> Evaluate(IEnumerable<TripResult> trips, ScoreSettings s)
    {
        var c = new AchievementCounters();
        var unlocked = new Dictionary<string, (DateTime At, long TripId)>();

        foreach (var t in trips.OrderBy(t => t.FinishedAt).ThenBy(t => t.Id))
        {
            Count(c, t, s);
            foreach (var def in All)
                if (!unlocked.ContainsKey(def.Id) && def.Value(c) >= def.Target)
                    unlocked[def.Id] = (t.FinishedAt, t.Id);
        }

        return All.Select(def => unlocked.TryGetValue(def.Id, out var u)
                ? new AchievementStatus(def, def.Value(c), u.At, u.TripId)
                : new AchievementStatus(def, def.Value(c), null, null))
            .ToList();
    }

    private static void Count(AchievementCounters c, TripResult t, ScoreSettings s)
    {
        // Штраф обнуляет серию и в отменённом рейсе
        if (t.FinesCount > 0) c.KmNoFines = 0;
        if (t.Status != TripStatus.Delivered) return;

        double km = t.DistanceKm;
        bool perfect = t.CargoDamage < s.PerfectDamageThreshold;

        c.Delivered++;
        c.TotalKm += km;
        c.LongestKm = Math.Max(c.LongestKm, km);
        c.Salary += t.Salary;

        if (perfect) { c.Perfect++; c.PerfectStreak++; }
        else c.PerfectStreak = 0;

        if (t.FinesCount == 0) c.KmNoFines += km;
        c.KmNoSpeeding = t.SpeedingSeconds <= s.CleanSpeedingSecondsMax ? c.KmNoSpeeding + km : 0;

        if (t.CargoClass == CargoClass.Adr || t.Traits.HasFlag(CargoTraits.Dangerous)) c.Adr++;
        if (perfect && (t.CargoClass == CargoClass.Fragile || t.Traits.HasFlag(CargoTraits.Fragile))) c.FragilePerfect++;
        if (t.CargoMassT >= 30 || t.CargoClass is CargoClass.Heavy or CargoClass.HeavyHaul) c.Heavy++;
        if (t.SpecialJob || t.CargoClass == CargoClass.HeavyHaul) c.Special++;
        if (t.TrailerCount > 1 || t.CargoClass == CargoClass.Articulated) c.Articulated++;
        if (t.Kind == TripKind.Convoy) c.Convoys++;
        if (t.Grade == "S") c.GradeS++;
        if (t.DrivenKm >= 300 && t.FuelPer100Km is > 0 and < 25) c.Eco++;
    }

    /// <summary>Какие достижения открылись именно этим рейсом.</summary>
    public static List<AchievementDef> UnlockedBy(IEnumerable<AchievementStatus> statuses, long tripId)
        => statuses.Where(a => a.UnlockedTripId == tripId).Select(a => a.Def).ToList();
}
