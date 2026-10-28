using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace EtsTracker;

/// <summary>
/// Настройки начисления очков. Сохраняются в data/scoring.json —
/// меняйте цифры там, перекомпилировать ничего не нужно.
/// </summary>
public sealed class ScoreSettings
{
    // --- Основа ---
    public double PointsPerKm { get; set; } = 1.0;

    // --- Масса: множитель растёт линейно до MassCapTonnes ---
    public double MassCapTonnes { get; set; } = 40;
    public double MaxMassBonus { get; set; } = 0.5;          // до ×1.5 за 40 т

    // --- Тип груза (бонусы складываются) ---
    public double FragileBonus { get; set; } = 0.30;
    public double ValuableBonus { get; set; } = 0.25;
    public double DangerousBonus { get; set; } = 0.40;
    public double SpecialJobBonus { get; set; } = 0.30;      // спецтранспорт

    // --- Повреждения: 10% урона при факторе 2.0 = −20% очков ---
    public double DamagePenaltyFactor { get; set; } = 2.0;
    public double PerfectDamageThreshold { get; set; } = 0.005; // меньше 0.5% — «без царапин»
    public double PerfectBonusPercent { get; set; } = 0.10;

    // --- Топливо: норма = FuelNormBase + FuelNormPerTonne × масса ---
    public double FuelNormBase { get; set; } = 28;
    public double FuelNormPerTonne { get; set; } = 0.4;
    public double MaxFuelBonusPercent { get; set; } = 0.20;
    public double MinKmForFuelBonus { get; set; } = 20;

    // --- Прочие бонусы ---
    public double ManualParkingBonus { get; set; } = 50;
    public double CleanDrivingBonus { get; set; } = 50;      // ни штрафов, ни превышений

    // --- Штрафы ---
    public double PointsPerFine { get; set; } = 100;
    public double SpeedingToleranceKph { get; set; } = 5;
    public double PointsPerSpeedingSecond { get; set; } = 1;
    public double CleanSpeedingSecondsMax { get; set; } = 30;
    public double CancelledJobPoints { get; set; } = -100;
}

/// <summary>
/// Признаки грузов (хрупкий / ценный / опасный). SDK их не передаёт,
/// поэтому определяем по ключевым словам в ID груза + точные правила.
/// Файл: data/cargo.json.
/// </summary>
public sealed class CargoCatalog
{
    public List<string> FragileKeywords { get; set; } = new()
    {
        "glass", "window", "electron", "comput", "ceramic", "porcelain", "tile",
        "pharma", "medic", "vaccin", "lamp", "tv", "furniture", "eggs", "wine",
    };

    public List<string> ValuableKeywords { get; set; } = new()
    {
        "electron", "comput", "pharma", "medic", "vaccin", "luxury", "yacht",
        "aircraft", "engine", "tractor", "excavat", "machin", "car_", "cars",
    };

    public List<string> DangerousKeywords { get; set; } = new()
    {
        "acid", "fuel", "petrol", "diesel", "gas", "lpg", "propane", "explos",
        "chlor", "cyan", "nitr", "toxic", "radio", "ammon", "fertil", "hydrogen",
        "pesticid", "paint", "solvent", "oxygen",
    };

    /// <summary>Точные правила по ID груза, важнее ключевых слов.
    /// Пример: "glass_pck": ["Fragile"], "cement": []</summary>
    public Dictionary<string, List<CargoTraits>> Overrides { get; set; } = new();

    public CargoTraits Classify(string cargoId, string cargoName)
    {
        if (Overrides.TryGetValue(cargoId, out var exact))
            return exact.Aggregate(CargoTraits.None, (acc, t) => acc | t);

        var text = $"{cargoId} {cargoName}".ToLowerInvariant();
        var traits = CargoTraits.None;
        if (FragileKeywords.Any(text.Contains)) traits |= CargoTraits.Fragile;
        if (ValuableKeywords.Any(text.Contains)) traits |= CargoTraits.Valuable;
        if (DangerousKeywords.Any(text.Contains)) traits |= CargoTraits.Dangerous;
        return traits;
    }
}

public static class ConfigStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save<T>(string path, T value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));

    /// <summary>Загружает JSON; если файла нет — создаёт его со значениями по умолчанию.</summary>
    public static T LoadOrCreate<T>(string path) where T : new()
    {
        if (File.Exists(path))
        {
            try
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? new T();
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"⚠ Ошибка в {Path.GetFileName(path)}: {ex.Message}. Использую значения по умолчанию.");
                return new T();
            }
        }

        var value = new T();
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
        return value;
    }
}
