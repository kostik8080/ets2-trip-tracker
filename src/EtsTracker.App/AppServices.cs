using System.IO;
using SCSSdkClient;

namespace EtsTracker.App;

/// <summary>
/// Всё, что живёт всё время работы программы: настройки, база, трекер и подключение к игре.
/// Файлы данных лежат рядом с программой, в папке data.
/// </summary>
public sealed class AppServices : IDisposable
{
    private SCSSdkTelemetry? _telemetry;

    public string DataDir { get; }
    public ScoreSettings Scoring { get; }
    public CargoCatalog Catalog { get; }
    public CompanySettings Company { get; }
    public TripRepository Repo { get; }
    public CareerService Career { get; }
    public TripTracker Tracker { get; }
    public AppState State { get; }
    /// <summary>Почему не удалось подключиться к телеметрии (null — всё в порядке).</summary>
    public string? TelemetryError { get; private set; }

    public AppServices()
    {
        DataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(DataDir);

        Scoring = ConfigStore.LoadOrCreate<ScoreSettings>(Path.Combine(DataDir, "scoring.json"));
        Catalog = ConfigStore.LoadOrCreate<CargoCatalog>(Path.Combine(DataDir, "cargo.json"));
        Company = ConfigStore.LoadOrCreate<CompanySettings>(Path.Combine(DataDir, "company.json"));
        State = ConfigStore.LoadOrCreate<AppState>(AppState.PathIn(DataDir));
        Repo = new TripRepository(Path.Combine(DataDir, "trips.db"));
        Career = new CareerService(Repo, Company);
        Tracker = new TripTracker(Catalog, Scoring);
    }

    /// <summary>Подключается к плагину телеметрии. Игра может быть ещё не запущена — это нормально.</summary>
    public void StartTelemetry()
    {
        if (_telemetry != null) return;
        _telemetry = new SCSSdkTelemetry();
        if (_telemetry.Error != null)
        {
            TelemetryError = _telemetry.Error.Message;
            return;
        }
        _telemetry.Data += (data, _) =>
        {
            try { Tracker.Process(TelemetryMapper.Map(data)); }
            catch (Exception) { /* битый кадр телеметрии — пропускаем */ }
        };
    }

    /// <summary>Отдельный трекер для демо-рейса, чтобы он не смешивался с данными игры.</summary>
    public TripTracker CreateDemoTracker() => new(Catalog, Scoring);

    public void RememberDriver(Driver d)
    {
        State.LastDriverId = d.Id;
        State.Save(DataDir);
    }

    public void Dispose() => _telemetry?.Dispose();
}
