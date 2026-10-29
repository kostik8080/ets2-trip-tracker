using System.Text;
using EtsTracker;
using SCSSdkClient;

Console.OutputEncoding = Encoding.UTF8;

// --diag: печатает сырые значения времени и навигации из телеметрии (ничего не записывает)
if (args.Contains("--diag"))
{
    using var diag = new SCSSdkTelemetry();
    var printed = 0;
    var done = new ManualResetEventSlim();
    diag.Data += (d, _) =>
    {
        if (!d.SdkActive || Interlocked.Increment(ref printed) > 3) { if (printed > 3) done.Set(); return; }
        Console.WriteLine(
            $"GameTime={d.CommonValues.GameTime.Value} ({d.CommonValues.GameTime.Date:ddd HH:mm}) " +
            $"Scale={d.CommonValues.Scale} DeliveryTime={d.JobValues.DeliveryTime.Value} " +
            $"RemainingDeliveryTime={d.JobValues.RemainingDeliveryTime.Value} " +
            $"NavDistance={d.NavigationValues.NavigationDistance:0}m NavTime={d.NavigationValues.NavigationTime:0}s " +
            $"Market={d.JobValues.Market} OnJob={d.SpecialEventsValues.OnJob} CargoLoaded={d.JobValues.CargoLoaded}");
        var f = TelemetryMapper.Map(d);
        Console.WriteLine(
            $"  → трекер: срок {(f.HasDeadline ? $"{f.DeadlineMinutesLeft} игр. мин" : "не передан игрой")}, " +
            $"в пути {f.NavTimeSec / 60:0} игр. мин ≈ {f.NavTimeSec / 60 / 19:0} реальных мин");
        Thread.Sleep(1000);
    };
    if (!done.Wait(TimeSpan.FromSeconds(15))) Console.WriteLine("Нет данных: игра не запущена или плагин не загружен.");
    return;
}

// --- Файлы данных лежат рядом с программой, в папке data ---
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);

var settings = ConfigStore.LoadOrCreate<ScoreSettings>(Path.Combine(dataDir, "scoring.json"));
var catalog = ConfigStore.LoadOrCreate<CargoCatalog>(Path.Combine(dataDir, "cargo.json"));
var company = ConfigStore.LoadOrCreate<CompanySettings>(Path.Combine(dataDir, "company.json"));
var repo = new TripRepository(Path.Combine(dataDir, "trips.db"));
var career = new CareerService(repo, company);
var tracker = new TripTracker(catalog, settings);
var ui = new ConsoleUi();

// Рейсы пишутся водителю, который последним был выбран в окне трекера
var state = File.Exists(AppState.PathIn(dataDir)) ? ConfigStore.LoadOrCreate<AppState>(AppState.PathIn(dataDir)) : new AppState();
var driver = repo.GetDrivers().FirstOrDefault(d => d.Id == state.LastDriverId);

tracker.TripStarted += _ => ui.Print("🚚 Новый заказ — запись рейса началась.");
tracker.Notice += msg => ui.Print(msg);
tracker.TripFinished += trip =>
{
    career.Record(trip, driver, TripKind.Solo);
    ui.PrintTrip(trip);
    ui.PrintStats(repo.GetStats(driver?.Id));
};

ui.PrintHeader();
ui.Print(driver != null
    ? $"👤 Водитель: {driver.Nickname} ({Names.Of(driver.Rank)})"
    : "👤 Водитель не выбран: рейсы запишутся без водителя и без зарплаты.");
ui.PrintStats(repo.GetStats(driver?.Id));

if (args.Contains("--demo"))
{
    ui.Print("Демо-режим: имитирую рейс без игры…\n");
    DemoTrip.Run(tracker);
    return;
}

if (args.Contains("--history"))
{
    ui.PrintHistory(repo.GetTrips(driver?.Id, 20));
    return;
}

// --- Реальная игра ---
using var telemetry = new SCSSdkTelemetry();
if (telemetry.Error != null)
{
    Console.WriteLine($"❌ Не удалось подключиться к телеметрии: {telemetry.Error.Message}");
    return;
}

telemetry.Data += (data, _) =>
{
    try { tracker.Process(TelemetryMapper.Map(data)); }
    catch (Exception ex) { ui.Print($"⚠ Ошибка обработки телеметрии: {ex.Message}"); }
};

ui.Print("Жду запуска Euro Truck Simulator 2… (Ctrl+C — выход)\n");

var exit = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };

while (!exit.Wait(TimeSpan.FromSeconds(1)))
    ui.UpdateStatus(tracker);

ui.Print("\nДо встречи на дорогах! 👋");
