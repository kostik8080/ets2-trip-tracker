namespace EtsTracker.App;

public enum TimeMode
{
    Real,   // реальное время
    Game,   // игровое время
}

/// <summary>Настройки оверлея поверх игры. Файл data/overlay.json.</summary>
public sealed class OverlaySettings
{
    public bool Enabled { get; set; }
    /// <summary>Показывать оверлей и без заказа (часы и скорость).</summary>
    public bool ShowWithoutJob { get; set; } = true;
    /// <summary>Непрозрачность фона, 0..1 (0 — фона нет, только текст).</summary>
    public double BackgroundOpacity { get; set; } = 0.55;
    public string TextColor { get; set; } = "#FFFFFF";
    public double FontSize { get; set; } = 15;
    public double Left { get; set; } = 40;
    public double Top { get; set; } = 160;
    /// <summary>В каком времени показывать часы, время в пути и срок доставки.</summary>
    public TimeMode TimeMode { get; set; } = TimeMode.Real;
}

/// <summary>
/// Показ времени в реальном или игровом исчислении.
/// Игра отдаёт время в пути и срок доставки в игровых минутах; реальное = игровое / масштаб.
/// </summary>
public sealed class TimeView
{
    private readonly TimeMode _mode;
    private readonly double _scale;
    private readonly uint _gameNow;

    public TimeView(TimeMode mode, TelemetryFrame? f)
    {
        _mode = mode;
        _scale = f?.TimeScale > 0.5 ? f.TimeScale : 19; // в ETS2 игровое время идёт ~в 19 раз быстрее
        _gameNow = f?.GameTimeMinutes ?? 0;
    }

    public bool IsReal => _mode == TimeMode.Real;
    public string Kind => IsReal ? "реального времени" : "игрового времени";

    /// <summary>Часы: реальные или игровые («Пн 14:35»).</summary>
    public string Clock() => IsReal ? DateTime.Now.ToString("HH:mm") : GameClock(_gameNow);

    /// <summary>Длительность, заданная в игровых минутах, — в выбранном времени.</summary>
    public string Duration(double gameMinutes) => Format(IsReal ? gameMinutes / _scale : gameMinutes);

    /// <summary>Когда закончится промежуток в игровых минутах — по выбранным часам.</summary>
    public string ClockAfter(double gameMinutes)
        => IsReal ? DateTime.Now.AddMinutes(gameMinutes / _scale).ToString("HH:mm")
                  : GameClock(_gameNow + (uint)Math.Max(0, gameMinutes));

    private static string GameClock(uint minutes)
    {
        var d = new DateTime((long)minutes * TimeSpan.TicksPerMinute);
        return $"{DayShort(d.DayOfWeek)} {d:HH:mm}";
    }

    private static string DayShort(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "Пн", DayOfWeek.Tuesday => "Вт", DayOfWeek.Wednesday => "Ср",
        DayOfWeek.Thursday => "Чт", DayOfWeek.Friday => "Пт", DayOfWeek.Saturday => "Сб", _ => "Вс",
    };

    /// <summary>Минуты → «2 ч 05 мин».</summary>
    public static string Format(double minutes)
    {
        var total = (int)Math.Round(minutes);
        if (total >= 24 * 60) return $"{total / (24 * 60)} д {total % (24 * 60) / 60} ч";
        return total >= 60 ? $"{total / 60} ч {total % 60:00} мин" : $"{total} мин";
    }

    /// <summary>«Хельсинки (Lisette Logistics)» → «Хельсинки».</summary>
    public static string City(string place)
    {
        int i = place.IndexOf(" (", StringComparison.Ordinal);
        return i > 0 ? place[..i] : place;
    }
}
