using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    public OverlaySettings Overlay { get; }
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
        Overlay = ConfigStore.LoadOrCreate<OverlaySettings>(Path.Combine(DataDir, "overlay.json"));
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

    /// <summary>Логотип компании: свой файл из data или встроенный.</summary>
    public ImageSource LoadLogo()
    {
        var file = string.IsNullOrEmpty(Company.LogoFile) ? null : Path.Combine(DataDir, Company.LogoFile);
        if (file != null && File.Exists(file))
        {
            try { return LoadImage(new Uri(file)); }
            catch (Exception) { /* битая картинка — показываем встроенную */ }
        }
        return LoadImage(new Uri("pack://application:,,,/Assets/logo.png"));
    }

    private static BitmapImage LoadImage(Uri uri)
    {
        // OnLoad — чтобы файл не оставался открытым и его можно было заменить
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.UriSource = uri;
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>Сохраняет название и логотип. Новый логотип копируется в data, чтобы не зависеть от исходного файла.</summary>
    public void SaveCompanyLook(string name, string? newLogoPath, bool resetLogo)
    {
        Company.CompanyName = name;
        if (resetLogo)
        {
            Company.LogoFile = "";
        }
        else if (newLogoPath != null)
        {
            var fileName = "logo" + Path.GetExtension(newLogoPath).ToLowerInvariant();
            File.Copy(newLogoPath, Path.Combine(DataDir, fileName), overwrite: true);
            Company.LogoFile = fileName;
        }
        ConfigStore.Save(Path.Combine(DataDir, "company.json"), Company);
    }

    public void SaveOverlay() => ConfigStore.Save(Path.Combine(DataDir, "overlay.json"), Overlay);

    public void RememberDriver(Driver d)
    {
        State.LastDriverId = d.Id;
        State.Save(DataDir);
    }

    public void Dispose() => _telemetry?.Dispose();
}
