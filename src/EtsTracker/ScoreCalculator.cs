namespace EtsTracker;

/// <summary>
/// Считает очки за рейс и заполняет расшифровку.
///
///   ядро  = км × очки_за_км × множитель_массы × множитель_груза × множитель_целости
///   итог  = ядро + бонусы − штрафы   (не меньше 0)
/// </summary>
public sealed class ScoreCalculator
{
    private readonly ScoreSettings _s;

    public ScoreCalculator(ScoreSettings settings) => _s = settings;

    public void Apply(TripResult trip)
    {
        var lines = new List<ScoreLine>();

        if (trip.Status == TripStatus.Cancelled)
        {
            lines.Add(new("Заказ отменён", _s.CancelledJobPoints));
            trip.Breakdown = lines;
            trip.Score = (int)Math.Round(_s.CancelledJobPoints);
            trip.Grade = "—";
            return;
        }

        // 1. База за расстояние (дистанция заказа; если игра её не дала — по одометру)
        double km = trip.JobDistanceKm > 0 ? trip.JobDistanceKm : trip.DrivenKm;
        double basePts = km * _s.PointsPerKm;
        lines.Add(new($"Дистанция {km:0} км", basePts));

        // 2. Масса
        double massShare = Math.Clamp(trip.CargoMassT / _s.MassCapTonnes, 0, 1);
        double massMult = 1 + massShare * _s.MaxMassBonus;

        // 3. Тип груза
        double cargoMult = 1;
        var cargoParts = new List<string>();
        if (trip.Traits.HasFlag(CargoTraits.Fragile)) { cargoMult += _s.FragileBonus; cargoParts.Add("хрупкий"); }
        if (trip.Traits.HasFlag(CargoTraits.Valuable)) { cargoMult += _s.ValuableBonus; cargoParts.Add("ценный"); }
        if (trip.Traits.HasFlag(CargoTraits.Dangerous)) { cargoMult += _s.DangerousBonus; cargoParts.Add("опасный"); }
        if (trip.SpecialJob) { cargoMult += _s.SpecialJobBonus; cargoParts.Add("спецтранспорт"); }

        // 4. Повреждения
        double damageMult = Math.Clamp(1 - trip.CargoDamage * _s.DamagePenaltyFactor, 0, 1);

        double core = basePts * massMult * cargoMult * damageMult;
        AddDelta(lines, $"Масса {trip.CargoMassT:0.#} т (×{massMult:0.00})", basePts * (massMult - 1));
        if (cargoParts.Count > 0)
            AddDelta(lines, $"Груз: {string.Join(", ", cargoParts)} (×{cargoMult:0.00})",
                basePts * massMult * (cargoMult - 1));
        if (damageMult < 1)
            AddDelta(lines, $"Повреждение груза {trip.CargoDamage:P1} (×{damageMult:0.00})",
                -basePts * massMult * cargoMult * (1 - damageMult));

        double total = core;

        // 5. Бонусы
        if (trip.CargoDamage < _s.PerfectDamageThreshold)
            total += Add(lines, "Груз без единой царапины", core * _s.PerfectBonusPercent);

        double fuelNorm = _s.FuelNormBase + _s.FuelNormPerTonne * trip.CargoMassT;
        if (trip.DrivenKm >= _s.MinKmForFuelBonus && trip.FuelPer100Km > 0 && trip.FuelPer100Km < fuelNorm)
        {
            double saving = (fuelNorm - trip.FuelPer100Km) / fuelNorm;
            total += Add(lines, $"Экономия топлива {trip.FuelPer100Km:0.0} л/100 при норме {fuelNorm:0.0}",
                core * Math.Min(saving, _s.MaxFuelBonusPercent));
        }

        if (!trip.AutoParked)
            total += Add(lines, "Ручная парковка", _s.ManualParkingBonus);

        if (trip.FinesCount == 0 && trip.SpeedingSeconds <= _s.CleanSpeedingSecondsMax)
            total += Add(lines, "Чистая езда (без штрафов и превышений)", _s.CleanDrivingBonus);

        // 6. Штрафы
        if (trip.FinesCount > 0)
            total += Add(lines, $"Штрафы ×{trip.FinesCount}", -trip.FinesCount * _s.PointsPerFine);

        if (trip.SpeedingSeconds >= 1)
            total += Add(lines, $"Превышение скорости {trip.SpeedingSeconds:0} с",
                -trip.SpeedingSeconds * _s.PointsPerSpeedingSecond);

        trip.Score = (int)Math.Round(Math.Max(0, total));
        trip.Breakdown = lines;
        trip.Grade = GradeFor(trip.Score, km);
    }

    /// <summary>Оценка по «очкам на километр».</summary>
    private static string GradeFor(int score, double km)
    {
        if (km < 1) return "—";
        double perKm = score / km;
        return perKm switch
        {
            >= 1.8 => "S",
            >= 1.4 => "A",
            >= 1.1 => "B",
            >= 0.8 => "C",
            _ => "D",
        };
    }

    private static double Add(List<ScoreLine> lines, string label, double pts)
    {
        lines.Add(new(label, pts));
        return pts;
    }

    private static void AddDelta(List<ScoreLine> lines, string label, double pts)
    {
        if (Math.Abs(pts) >= 0.5) lines.Add(new(label, pts));
    }
}
