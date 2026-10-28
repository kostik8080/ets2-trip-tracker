namespace EtsTracker;

/// <summary>Простой консольный интерфейс. Потом его можно заменить на окно (WPF/Avalonia).</summary>
public sealed class ConsoleUi
{
    private readonly object _lock = new();
    private bool _statusShown;

    public void PrintHeader()
    {
        Print("""
            ╔══════════════════════════════════════╗
            ║        ETS2 Trip Tracker  v0.2       ║
            ╚══════════════════════════════════════╝
            """);
    }

    public void Print(string text)
    {
        lock (_lock)
        {
            ClearStatus();
            Console.WriteLine(text);
        }
    }

    /// <summary>Строка состояния, перерисовывается раз в секунду.</summary>
    public void UpdateStatus(TripTracker tracker)
    {
        var f = tracker.LastFrame;
        string line;

        if (f == null || DateTime.Now - tracker.LastFrameAt > TimeSpan.FromSeconds(5) || !f.SdkActive)
            line = "⏳ Игра не запущена или в главном меню…";
        else if (f.Paused)
            line = "⏸  Пауза";
        else if (tracker.Current is { } t)
            line = $"🚚 {t.CargoName} {t.CargoMassKg / 1000:0.#} т | {t.From} → {t.To} | " +
                   (t.CargoLoaded ? $"{t.Progress:P0}, осталось {t.RemainingKm:0} км" : "едем за грузом") +
                   $" | ⛽ {t.FuelUsedL:0} л | урон {f.CargoDamage:P1} | " +
                   $"{f.SpeedKph:0}/{(f.SpeedLimitKph > 1 ? f.SpeedLimitKph.ToString("0") : "—")} км/ч";
        else
            line = $"🅿  Без заказа | {f.SpeedKph:0} км/ч";

        lock (_lock)
        {
            int width = Math.Max(20, SafeWindowWidth() - 1);
            if (line.Length > width) line = line[..width];
            Console.Write("\r" + line.PadRight(width));
            _statusShown = true;
        }
    }

    public void PrintTrip(TripResult t)
    {
        lock (_lock)
        {
            ClearStatus();
            Console.WriteLine();
            if (t.Status == TripStatus.Cancelled)
            {
                Console.WriteLine($"❌ Заказ отменён: {t.CargoName}  ({t.From} → {t.To})");
                Console.WriteLine($"   Очки: {t.Score}");
                Console.WriteLine();
                return;
            }

            Console.WriteLine("════════════ РЕЙС ЗАВЕРШЁН ════════════");
            Console.WriteLine($" Груз:      {t.CargoName}, {t.CargoMassT:0.#} т {TraitsText(t)}");
            Console.WriteLine($" Маршрут:   {t.From} → {t.To}");
            Console.WriteLine($" Дистанция: {t.JobDistanceKm:0} км (проехано всего {t.DrivenKm:0} км)");
            Console.WriteLine($" Топливо:   {t.FuelUsedL:0.0} л ({t.FuelPer100Km:0.0} л/100 км)");
            Console.WriteLine($" Урон:      груз {t.CargoDamage:P1}, тягач {t.TruckDamage:P1}, прицеп {t.TrailerDamage:P1}");
            Console.WriteLine($" Заработок: €{t.Revenue:N0}   Опыт: +{t.EarnedXp}");
            if (t.FinesCount > 0)
                Console.WriteLine($" Штрафы:    {t.FinesCount} шт. на €{t.FinesTotal:N0}");
            if (t.StartedMidJob)
                Console.WriteLine(" ⚠ Трекер запущен посреди рейса — пробег и топливо неполные.");
            Console.WriteLine("──────────────── Очки ─────────────────");
            foreach (var line in t.Breakdown)
                Console.WriteLine($" {line.Label,-46} {line.Points,7:+0;-0;0}");
            Console.WriteLine("───────────────────────────────────────");
            Console.WriteLine($" ИТОГО: {t.Score} очков   Оценка: {t.Grade}");
            Console.WriteLine($"────────── Зарплата ({Names.Of(t.CargoClass)}) ──────────");
            foreach (var line in t.SalaryBreakdown)
                Console.WriteLine($" {line.Label,-46} {line.Points,9:N0}");
            Console.WriteLine($" ИТОГО: €{t.Salary:N0}");
            Console.WriteLine("═══════════════════════════════════════");
            Console.WriteLine();
        }
    }

    public void PrintStats(CareerStats s)
    {
        var lvl = DriverLevel.From(s.TotalScore);
        Print($"🏆 Уровень {lvl.Level} | очков: {s.TotalScore:N0} (до следующего: {lvl.NextLevelAt - lvl.Score:N0}) | " +
              $"рейсов: {s.Delivered}/{s.Trips} | {s.TotalKm:N0} км | {s.TotalTonnes:N0} т | " +
              $"⛽ {s.TotalFuelL:N0} л | €{s.TotalRevenue:N0} | без царапин: {s.PerfectDeliveries} | штрафов: {s.TotalFines}\n");
    }

    public void PrintHistory(List<TripResult> trips)
    {
        Print("Последние рейсы:");
        foreach (var t in trips)
            Print($"  {t.FinishedAt:dd.MM HH:mm}  [{t.Grade}] {t.Score,6}  €{t.Salary,9:N0}  {t.CargoName}  {t.From} → {t.To}");
        if (trips.Count == 0) Print("  пока пусто");
    }

    private static string TraitsText(TripResult t)
    {
        var parts = new List<string>();
        if (t.Traits.HasFlag(CargoTraits.Fragile)) parts.Add("хрупкий");
        if (t.Traits.HasFlag(CargoTraits.Valuable)) parts.Add("ценный");
        if (t.Traits.HasFlag(CargoTraits.Dangerous)) parts.Add("опасный");
        if (t.SpecialJob) parts.Add("спецтранспорт");
        return parts.Count > 0 ? $"[{string.Join(", ", parts)}]" : "";
    }

    private void ClearStatus()
    {
        if (!_statusShown) return;
        Console.Write("\r" + new string(' ', Math.Max(20, SafeWindowWidth() - 1)) + "\r");
        _statusShown = false;
    }

    private static int SafeWindowWidth()
    {
        try { return Console.WindowWidth > 0 ? Console.WindowWidth : 120; }
        catch { return 120; }
    }
}
