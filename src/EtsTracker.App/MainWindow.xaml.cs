using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace EtsTracker.App;

public partial class MainWindow : Window
{
    private readonly AppServices _s;
    private readonly DispatcherTimer _timer;
    private readonly OverlayWindow _overlay;
    private OverlaySettingsWindow? _overlaySettings;
    private readonly ObservableCollection<string> _notices = new();
    private Driver _driver;
    private List<TripResult> _trips = new();
    private TripTracker? _demoTracker;

    public MainWindow(AppServices services, Driver driver)
    {
        InitializeComponent();
        _s = services;
        _driver = driver;

        NoticesList.ItemsSource = _notices;
        KindBox.ItemsSource = Choices.TripKinds;
        ClassBox.ItemsSource = Choices.CargoClasses;
        PeriodBox.SelectedIndex = 0;

        // События трекера приходят из потока телеметрии — переносим их в поток окна
        _s.Tracker.TripStarted += _ => Dispatcher.BeginInvoke(() => AddNotice("🚚 Новый заказ — запись рейса началась."));
        _s.Tracker.Notice += msg => Dispatcher.BeginInvoke(() => AddNotice(msg));
        _s.Tracker.TripFinished += trip => Dispatcher.BeginInvoke(() => OnTripFinished(trip));
        _s.StartTelemetry();

        _overlay = new OverlayWindow(_s);

        // Полсекунды — чтобы скорость в оверлее не отставала
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => UpdateLive();
        _timer.Start();
        SourceInitialized += (_, _) => RegisterOverlayHotkey();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _overlay.Close();
        };

        ShowCompany();
        UpdatePlugin();
        Reload();
        InitRating();
        Loaded += async (_, _) => await ShowRatingAsync();
        UpdateLive();
    }

    // ───────────────────────────── Компания ─────────────────────────────

    private void ShowCompany()
    {
        CompanyText.Text = _s.Company.CompanyName;
        LogoImage.Source = _s.LoadLogo();
        ShowDriver();
    }

    private void OnEditCompany(object sender, RoutedEventArgs e)
    {
        if (!_driver.IsAdmin) return;
        if (new CompanyWindow(_s) { Owner = this }.ShowDialog() == true)
            ShowCompany();
    }

    // ───────────────────────────── Водитель ─────────────────────────────

    private void ShowDriver()
    {
        RegNumberText.Text = _driver.RegNumber;
        DriverNameText.Text = _driver.IsAdmin ? $"{_driver.Nickname}  ★" : _driver.Nickname;
        var info = new List<string> { Names.Of(_driver.Rank) };
        if (_driver.IsAdmin) info.Add("администратор");
        if (!string.IsNullOrEmpty(_driver.Truck)) info.Add(_driver.Truck);
        DriverInfoText.Text = string.Join(" · ", info);

        // Права администратора
        var admin = _driver.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        DriversButton.Visibility = admin;
        CompanyButton.Visibility = admin;
        TripAdminButtons.Visibility = admin;
        DemoButton.Visibility = admin; // демо-рейс пишет зарплату — только для администратора
        TripAdminHint.Visibility = _driver.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
        KindBox.IsEnabled = ClassBox.IsEnabled = _driver.IsAdmin;
        Title = $"{_s.Company.CompanyName} — {_driver.Nickname}";
    }

    private void OnEditProfile(object sender, RoutedEventArgs e)
    {
        var edit = new DriverEditWindow(_s, _driver, canManage: _driver.IsAdmin) { Owner = this };
        if (edit.ShowDialog() != true) return;
        ShowDriver();
        Reload(SelectedTrip?.Id);
    }

    private void OnManageDrivers(object sender, RoutedEventArgs e)
    {
        if (!_driver.IsAdmin) return;
        // После входа на сервер главный список — водители компании
        if (Account is { IsAdmin: true }) OnServerDrivers(sender, e);
        else OpenLocalProfiles(this);
    }

    private void OpenLocalProfiles(Window owner)
    {
        var window = new DriversWindow(_s, _driver.Id) { Owner = owner };
        window.ShowDialog();
        if (!window.Changed) return;
        // Текущего водителя могли изменить (ранг, роль) — перечитываем его из базы
        _driver = _s.Repo.GetDrivers().FirstOrDefault(d => d.Id == _driver.Id) ?? _driver;
        ShowDriver();
        Reload(SelectedTrip?.Id);
    }

    private void OnSwitchDriver(object sender, RoutedEventArgs e)
    {
        var select = new DriverSelectWindow(_s, _driver.Id) { Owner = this };
        if (select.ShowDialog() != true || select.Chosen == null) return;
        _driver = select.Chosen;
        ShowDriver();
        Reload();
        _ = ShowRatingAsync();
    }

    // ────────────────────────────── Оверлей ──────────────────────────────

    private void OnOverlaySettings(object sender, RoutedEventArgs e)
    {
        if (_overlaySettings != null)
        {
            _overlaySettings.Activate();
            return;
        }
        // Не модальное: пока оно открыто, оверлей можно перетаскивать мышью
        _overlaySettings = new OverlaySettingsWindow(_s, _overlay, UpdateLive) { Owner = this };
        _overlaySettings.Closed += (_, _) => _overlaySettings = null;
        _overlaySettings.Show();
    }

    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x4F56; // «OV»
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000, VK_O = 0x4F;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    /// <summary>Ctrl+Alt+O — показать или скрыть оверлей, не выходя из игры.</summary>
    private void RegisterOverlayHotkey()
    {
        var source = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        if (source == null || !RegisterHotKey(source.Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_O)) return;
        source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                _s.Overlay.Enabled = !_s.Overlay.Enabled;
                _s.SaveOverlay();
                UpdateLive();
                handled = true;
            }
            return IntPtr.Zero;
        });
    }

    // ───────────────────────── Плагин телеметрии ─────────────────────────

    private string? _gameDir;

    private void UpdatePlugin()
    {
        _gameDir = PluginInstaller.FindGame(_s.State.GameDir);
        var state = PluginInstaller.GetState(_gameDir);
        (string text, string brush, string? button) = state switch
        {
            PluginState.Installed => ("✔ Плагин телеметрии установлен", "GoodBrush", null),
            PluginState.NotInstalled => ("Плагин телеметрии не установлен — без него трекер не видит игру.", "BadBrush", "Установить плагин"),
            PluginState.Outdated => ("В игре другая версия плагина телеметрии.", "AccentBrush", "Обновить плагин"),
            _ => ("Папка Euro Truck Simulator 2 не найдена в Steam.", "MutedBrush", "Указать папку игры…"),
        };
        PluginText.Text = text;
        PluginText.Foreground = (Brush)FindResource(brush);
        PluginText.ToolTip = _gameDir;
        PluginButton.Content = button;
        PluginButton.Visibility = button == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPluginButton(object sender, RoutedEventArgs e)
    {
        if (_gameDir == null)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Папка Euro Truck Simulator 2" };
            if (dialog.ShowDialog(this) != true) return;
            if (!PluginInstaller.IsGameDir(dialog.FolderName))
            {
                MessageBox.Show(this, @"В этой папке нет игры: не найден bin\win_x64\eurotrucks2.exe." + "\n" +
                    "Выберите корневую папку «Euro Truck Simulator 2».", "Плагин", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _s.State.GameDir = dialog.FolderName;
            _s.State.Save(_s.DataDir);
            _gameDir = dialog.FolderName;
        }

        try
        {
            PluginInstaller.Install(_gameDir);
            AddNotice("✔ Плагин телеметрии установлен. При запуске игры нажмите «ОК» в окне про SDK.");
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Плагин", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdatePlugin();
    }

    // ─────────────────────────── Текущий рейс ───────────────────────────

    private void OnTripFinished(TripResult trip)
    {
        var kind = ConvoyBox.IsChecked == true ? TripKind.Convoy : TripKind.Solo;
        _s.Career.Record(trip, _driver, kind);
        ConvoyBox.IsChecked = false;

        AddNotice(trip.Status == TripStatus.Delivered
            ? $"✅ Рейс записан: {trip.Score} очков ({trip.Grade}), зарплата €{trip.Salary:N0}"
            : $"❌ Заказ отменён: {trip.Score} очков");
        Reload(trip.Id, announce: true);
        _ = SyncThenRefreshAsync();
    }

    private async Task SyncThenRefreshAsync()
    {
        await SyncAsync();
        await LoadLeaderboardAsync();
    }

    private void AddNotice(string text)
    {
        _notices.Insert(0, $"{DateTime.Now:HH:mm}  {text}");
        while (_notices.Count > 4) _notices.RemoveAt(_notices.Count - 1);
    }

    private void UpdateLive()
    {
        // Во время демо-рейса показываем его, а не данные игры
        var tracker = _demoTracker ?? _s.Tracker;
        var f = tracker.LastFrame;
        bool live = f != null && f.SdkActive && DateTime.Now - tracker.LastFrameAt <= TimeSpan.FromSeconds(5);
        UpdateProgress(live ? tracker.Current : null, f);
        _overlay.Update(live, f, live ? tracker.Current : null);
        PublishLive(live ? tracker.Current : null, f);

        if (_s.TelemetryError != null && _demoTracker == null)
            SetStatus("Нет связи с телеметрией", "BadBrush", _s.TelemetryError);
        else if (!live)
            SetStatus("Игра не запущена", "MutedBrush",
                "Запустите Euro Truck Simulator 2 — трекер подключится сам.");
        else if (f!.Paused)
            SetStatus("Пауза", "AccentBrush", tracker.Current != null ? TripText(tracker.Current, f) : "");
        else if (tracker.Current is { } trip)
            SetStatus($"В рейсе: {trip.CargoName}", "GoodBrush", TripText(trip, f));
        else
            SetStatus("Без заказа", "MutedBrush",
                $"Скорость {f.SpeedKph:0} км/ч. Возьмите заказ — рейс запишется автоматически.");
    }

    /// <summary>Подробности под шкалой рейса (маршрут и остаток пути — на самой шкале).</summary>
    private static string TripText(ActiveTrip t, TelemetryFrame f)
    {
        string limit = f.SpeedLimitKph > 1 ? f.SpeedLimitKph.ToString("0") : "—";
        return $"{t.CargoMassKg / 1000:0.#} т · всего проехано {t.DrivenKm:0} км\n" +
               $"Топливо {t.FuelUsedL:0} л · скорость {f.SpeedKph:0} / {limit} км/ч\n" +
               $"Урон: груз {f.CargoDamage:P1}, тягач {t.TruckDamage:P1}, прицеп {t.TrailerDamage:P1}" +
               (t.FinesCount > 0 ? $"\nШтрафов: {t.FinesCount} (€{t.FinesTotal:N0})" : "");
    }

    /// <summary>Шкала рейса: сколько пройдено и сколько осталось до финиша.</summary>
    private void UpdateProgress(ActiveTrip? t, TelemetryFrame? f)
    {
        if (t == null)
        {
            TripProgressPanel.Visibility = Visibility.Collapsed;
            return;
        }
        TripProgressPanel.Visibility = Visibility.Visible;
        ProgressFrom.Text = TimeView.City(t.From);
        ProgressTo.Text = TimeView.City(t.To);
        var time = new TimeView(_s.Overlay.TimeMode, f);

        if (!t.CargoLoaded)
        {
            ProgressStage.Text = "🚚 Едем за грузом";
            ProgressPercent.Text = "";
            TripBar.Value = 0;
            RemainingText.Text = t.NavRemainingKm is { } toCargo
                ? $"до погрузки {toCargo:N0} км"
                : "проложите маршрут в навигаторе";
        }
        else
        {
            ProgressStage.Text = "🚚 Везём груз";
            ProgressPercent.Text = t.Progress.ToString("P0");
            TripBar.Value = t.Progress * 100;
            RemainingText.Text = t.TotalKm > 0
                ? $"осталось {t.RemainingKm:N0} км из {t.TotalKm:N0}"
                : $"осталось {t.RemainingKm:N0} км";
        }

        EtaText.Text = t.NavEtaSec is { } eta
            ? $"В пути ещё ≈ {time.Duration(eta / 60)}, прибытие в {time.ClockAfter(eta / 60)} ({time.Kind})"
            : "";
        EtaText.Visibility = t.NavEtaSec != null ? Visibility.Visible : Visibility.Collapsed;

        if (t.DeadlineMinutesLeft is { } left)
        {
            DeadlineText.Text = left >= 0
                ? $"До срока доставки: {time.Duration(left)} {time.Kind}"
                : $"⚠ Опоздание: {time.Duration(-left)} {time.Kind}";
            DeadlineText.Foreground = (Brush)FindResource(left < 0 ? "BadBrush" : left < 60 ? "AccentBrush" : "TextBrush");
            DeadlineText.Visibility = Visibility.Visible;
        }
        else DeadlineText.Visibility = Visibility.Collapsed;
    }

    private void SetStatus(string title, string brushKey, string details)
    {
        StatusTitle.Text = title;
        StatusDot.Fill = (Brush)FindResource(brushKey);
        StatusDetails.Text = details;
        StatusDetails.Visibility = string.IsNullOrEmpty(details) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnDemo(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Демо-рейс за 20 секунд имитирует поездку Хельсинки → Турку без игры и запишется в историю водителя. " +
            "Потом его можно удалить на вкладке «Рейсы».\n\nЗапустить?",
            "Демо-рейс", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        DemoButton.IsEnabled = false;
        var demo = _s.CreateDemoTracker();
        demo.Notice += msg => Dispatcher.BeginInvoke(() => AddNotice(msg));
        demo.TripFinished += trip =>
        {
            trip.IsDemo = true; // демо-рейс на сервер не отправляется
            Dispatcher.BeginInvoke(() => OnTripFinished(trip));
        };
        AddNotice("Демо-рейс: имитирую поездку…");
        _demoTracker = demo;
        try { await Task.Run(() => DemoTrip.Run(demo, TimeSpan.FromSeconds(20))); }
        finally
        {
            _demoTracker = null;
            DemoButton.IsEnabled = true;
            UpdateLive();
        }
    }

    // ────────────────────────── Данные и вкладки ──────────────────────────

    /// <param name="announce">Сообщить о достижениях, открытых с прошлой загрузки (после рейса или правки).</param>
    private void Reload(long? selectTripId = null, bool announce = false)
    {
        _trips = _s.Repo.GetTrips(_driver.Id);
        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var month = DriverStats.Compute(_trips.Where(t => t.FinishedAt >= monthStart));
        var all = DriverStats.Compute(_trips);

        StatsGrid.ItemsSource = StatRows(month, all);

        SalaryMonthText.Text = $"€{month.Salary:N0}";
        MonthSummaryText.Text = $"{Plural(month.Delivered, "рейс", "рейса", "рейсов")} · {month.Km:N0} км · " +
                                 Plural(month.Score, "очко", "очка", "очков");

        var level = DriverLevel.From(all.Score);
        LevelText.Text = $"Уровень {level.Level}";
        LevelBar.Value = level.Progress * 100;
        LevelHint.Text = $"{all.Score:N0} очков · до уровня {level.Level + 1}: {level.NextLevelAt - level.Score:N0}";

        RankText.Text = Names.Of(_driver.Rank);
        if (_s.Company.NextRequirement(_driver.Rank) is { } next)
        {
            NextRankText.Text = $"До звания «{Names.Of(next.Rank)}»: конвоев {all.Convoys} из {next.Convoys} {next.Alternative}";
            RankBar.Value = next.Convoys > 0 ? Math.Min(100, all.Convoys * 100.0 / next.Convoys) : 100;
            RankBar.Visibility = Visibility.Visible;
        }
        else
        {
            NextRankText.Text = "Следующее звание присваивает инструктор.";
            RankBar.Visibility = Visibility.Collapsed;
        }
        PermitsText.Text = $"Допуски:  ADR {Mark(_driver.PermitAdr)}   Тяжёлые {Mark(_driver.PermitHeavy)}   " +
                           $"Сочленённые {Mark(_driver.PermitArticulated)}";

        var rows = _trips.Select(t => new TripRow(t)).ToList();
        TripsGrid.ItemsSource = rows;
        TripsGrid.SelectedItem = rows.FirstOrDefault(r => r.Trip.Id == selectTripId) ?? rows.FirstOrDefault();
        if (rows.Count == 0) ShowTrip(null);

        UpdateCharts();
        UpdateRates();
        UpdateAchievements(announce);
    }

    private static string Mark(bool ok) => ok ? "✔" : "✘";

    /// <summary>«1 рейс», «2 рейса», «5 рейсов».</summary>
    private static string Plural(long n, string one, string few, string many)
    {
        long m100 = Math.Abs(n) % 100, m10 = m100 % 10;
        string word = m100 is >= 11 and <= 14 ? many : m10 == 1 ? one : m10 is >= 2 and <= 4 ? few : many;
        return $"{n:N0} {word}";
    }

    private static List<StatRow> StatRows(DriverStats m, DriverStats a) => new()
    {
        new("Перевезено грузов", $"{m.Delivered}", $"{a.Delivered}"),
        new("Конвоев", $"{m.Convoys}", $"{a.Convoys}"),
        new("Одиночных поездок", $"{m.SoloTrips}", $"{a.SoloTrips}"),
        new("Отменено заказов", $"{m.Cancelled}", $"{a.Cancelled}"),
        new("Масса доставленных грузов", $"{m.Tonnes:N0} т", $"{a.Tonnes:N0} т"),
        new("Пройденное расстояние", $"{m.Km:N0} км", $"{a.Km:N0} км"),
        new("Расход топлива", $"{m.FuelL:N0} л", $"{a.FuelL:N0} л"),
        new("Средний вес груза", $"{m.AvgMassT:0.#} т", $"{a.AvgMassT:0.#} т"),
        new("Среднее расстояние", $"{m.AvgKm:N0} км", $"{a.AvgKm:N0} км"),
        new("Средний расход", $"{m.AvgFuelPer100:0.0} л/100 км", $"{a.AvgFuelPer100:0.0} л/100 км"),
        new("Урон тягачу, в среднем", $"{m.AvgTruckDamage:P1}", $"{a.AvgTruckDamage:P1}"),
        new("Урон прицепу, в среднем", $"{m.AvgTrailerDamage:P1}", $"{a.AvgTrailerDamage:P1}"),
        new("Урон грузу, в среднем", $"{m.AvgCargoDamage:P1}", $"{a.AvgCargoDamage:P1}"),
        new("Грузов без царапин", $"{m.Perfect}", $"{a.Perfect}"),
        new("Штрафов", $"{m.Fines}", $"{a.Fines}"),
        new("Доход в игре", $"€{m.Revenue:N0}", $"€{a.Revenue:N0}"),
        new("Зарплата ВТК", $"€{m.Salary:N0}", $"€{a.Salary:N0}"),
        new("Очки трекера", $"{m.Score:N0}", $"{a.Score:N0}"),
    };

    // ─────────────────────────────── Рейсы ───────────────────────────────

    private TripResult? SelectedTrip => (TripsGrid.SelectedItem as TripRow)?.Trip;

    private void OnTripSelected(object sender, SelectionChangedEventArgs e) => ShowTrip(SelectedTrip);

    private void ShowTrip(TripResult? t)
    {
        NoTripText.Visibility = t == null ? Visibility.Visible : Visibility.Collapsed;
        DetailsPanel.Visibility = t == null ? Visibility.Collapsed : Visibility.Visible;
        if (t == null) return;

        TripTitle.Text = $"{t.CargoName}, {t.CargoMassT:0.#} т";
        TripRoute.Text = $"{t.From} → {t.To}";

        var meta = new List<string>
        {
            $"{t.FinishedAt:dd.MM.yyyy HH:mm} · {t.TruckName}",
            $"Дистанция {t.DistanceKm:N0} км, проехано {t.DrivenKm:N0} км",
            $"Топливо {t.FuelUsedL:0.0} л ({t.FuelPer100Km:0.0} л/100 км)",
            $"Урон: груз {t.CargoDamage:P1}, тягач {t.TruckDamage:P1}, прицеп {t.TrailerDamage:P1}",
            $"Доход в игре €{t.Revenue:N0} · опыт +{t.EarnedXp}",
        };
        if (t.FinesCount > 0) meta.Add($"Штрафы: {t.FinesCount} на €{t.FinesTotal:N0}");
        if (t.StartedMidJob) meta.Add("⚠ Трекер запущен посреди рейса — пробег и топливо неполные.");
        if (MissingPermitText(t) is { } warn) meta.Add(warn);
        TripMeta.Text = string.Join("\n", meta);

        KindBox.SelectedValue = t.Kind;
        ClassBox.SelectedValue = t.CargoClass;

        ScoreLines.ItemsSource = t.Breakdown.Select(l => LineRow.Points(l.Label, l.Points)).ToList();
        ScoreTotal.Text = t.Status == TripStatus.Delivered
            ? $"Итого: {t.Score} очков · оценка {t.Grade}"
            : $"Заказ отменён: {t.Score} очков";
        SalaryLines.ItemsSource = t.SalaryBreakdown.Select(l => LineRow.Money(l.Label, l.Points)).ToList();
        SalaryTotal.Text = $"Итого: €{t.Salary:N0}";
    }

    private string? MissingPermitText(TripResult t)
        => PayCalculator.MissingPermit(_driver, t.CargoClass) is { } p ? $"⚠ У водителя нет допуска «{p}»" : null;

    private void OnReclassify(object sender, RoutedEventArgs e)
    {
        if (!_driver.IsAdmin || SelectedTrip is not { } t) return;
        var kind = KindBox.SelectedValue is TripKind k ? k : t.Kind;
        var cls = ClassBox.SelectedValue is CargoClass c ? c : t.CargoClass;
        _s.Career.Reclassify(t, _driver, kind, cls);
        Reload(t.Id, announce: true);
    }

    private void OnDeleteTrip(object sender, RoutedEventArgs e)
    {
        if (!_driver.IsAdmin || SelectedTrip is not { } t) return;
        var answer = MessageBox.Show(this,
            $"Удалить рейс «{t.CargoName}» ({t.From} → {t.To}) от {t.FinishedAt:dd.MM.yyyy HH:mm}?\nВернуть его будет нельзя.",
            "Удаление рейса", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _s.Repo.DeleteTrip(t.Id);
        Reload();
    }

    // ──────────────────────────── Достижения ────────────────────────────

    /// <summary>Открытые достижения на момент прошлой загрузки (для текущего водителя).</summary>
    private HashSet<string>? _unlockedIds;
    private long _achDriverId;

    private void UpdateAchievements(bool announce)
    {
        var all = Achievements.Evaluate(_trips, _s.Scoring);
        var unlocked = all.Where(a => a.Unlocked).ToList();

        // Новые достижения объявляем только у того же водителя и только после рейса или правки
        if (announce && _unlockedIds != null && _achDriverId == _driver.Id)
        {
            foreach (var a in unlocked.Where(a => !_unlockedIds.Contains(a.Def.Id)))
            {
                AddNotice($"🏆 Новое достижение: «{a.Def.Title}»");
                _overlay.Toast($"🏆 Достижение: {a.Def.Title}");
            }
        }
        _unlockedIds = unlocked.Select(a => a.Def.Id).ToHashSet();
        _achDriverId = _driver.Id;

        AchSummary.Text = $"Открыто {unlocked.Count} из {all.Count}";
        AchPercent.Text = $"{(double)unlocked.Count / all.Count:P0}";
        AchBar.Value = 100.0 * unlocked.Count / all.Count;
        var latest = unlocked.OrderByDescending(a => a.UnlockedAt).FirstOrDefault();
        var next = all.Where(a => !a.Unlocked).OrderByDescending(a => a.Progress).FirstOrDefault();
        var hints = new List<string>();
        if (latest != null) hints.Add($"Последнее: «{latest.Def.Title}», {latest.UnlockedAt:dd.MM.yyyy}");
        if (next != null) hints.Add($"Ближе всего: «{next.Def.Title}» — {AchRow.ProgressText(next)}");
        AchLatest.Text = string.Join(" · ", hints);

        AchList.ItemsSource = all.Select(a => new AchRow(a)).ToList();
    }

    private sealed record AchRow(AchievementStatus A)
    {
        private static readonly Brush Locked = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
        private static readonly Brush Open = new SolidColorBrush(Color.FromRgb(0x31, 0x47, 0x9D));
        private static readonly Brush OpenCard = new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFF));
        private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));

        public string Icon => A.Def.Icon;
        public string Title => A.Def.Title;
        public string Description => A.Def.Description;
        public double Percent => A.Progress * 100;
        public Visibility BarVisibility => A.Unlocked ? Visibility.Collapsed : Visibility.Visible;
        public Brush IconBrush => A.Unlocked ? Open : Locked;
        public Brush CardBrush => A.Unlocked ? OpenCard : Brushes.White;
        public double CardOpacity => A.Unlocked ? 1 : 0.8;
        public Brush StatusBrush => A.Unlocked ? Good : Locked;
        public string Status => A.Unlocked ? $"✔ Получено {A.UnlockedAt:dd.MM.yyyy}" : ProgressText(A);

        /// <summary>«640 / 1 000 км», «3 / 10 рейсов», «€12 300 / €100 000».</summary>
        public static string ProgressText(AchievementStatus a)
        {
            double cur = Math.Min(a.Current, a.Def.Target);
            return a.Def.Unit == "€"
                ? $"€{cur:N0} / €{a.Def.Target:N0}"
                : $"{cur:N0} / {a.Def.Target:N0} {a.Def.Unit}";
        }
    }

    // ────────────────────────────── Графики ──────────────────────────────

    private void OnPeriodChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) UpdateCharts();
    }

    private void UpdateCharts()
    {
        var delivered = _trips.Where(t => t.Status == TripStatus.Delivered).ToList();
        bool byMonth = PeriodBox.SelectedIndex == 1;

        List<DateTime> buckets = byMonth
            ? Enumerable.Range(0, 12).Select(i => FirstOfMonth(DateTime.Today).AddMonths(i - 11)).ToList()
            : Enumerable.Range(0, 30).Select(i => DateTime.Today.AddDays(i - 29)).ToList();
        DateTime Key(TripResult t) => byMonth ? FirstOfMonth(t.FinishedAt) : t.FinishedAt.Date;
        string Label(DateTime d) => byMonth ? d.ToString("MM.yy") : d.ToString("dd.MM");

        var groups = delivered.GroupBy(Key).ToDictionary(g => g.Key, g => g.ToList());
        List<ChartPoint> Series(Func<TripResult, double> value) => buckets
            .Select(b => new ChartPoint(Label(b), groups.TryGetValue(b, out var list) ? list.Sum(value) : 0))
            .ToList();

        KmChart.Points = Series(t => t.DistanceKm);
        SalaryChart.Points = Series(t => t.Salary);
        ScoreChart.Points = Series(t => t.Score);
    }

    private static DateTime FirstOfMonth(DateTime d) => new(d.Year, d.Month, 1);

    // ─────────────────────────────── Ставки ───────────────────────────────

    private void UpdateRates()
    {
        var c = _s.Company;
        double prev = 0;
        var rates = new List<LineRow>();
        foreach (var r in c.DistanceRates.OrderBy(r => r.UpToKm ?? double.MaxValue))
        {
            string label = r.UpToKm is { } upTo ? $"Рейс до {upTo:N0} км" : $"Рейс более {prev:N0} км";
            rates.Add(new(label, $"€{r.RatePerKm:0.0#} за км", null));
            prev = r.UpToKm ?? prev;
        }
        RatesList.ItemsSource = rates;

        BonusTitle.Text = $"НАДБАВКА ЗА КЛАСС ГРУЗА — {Names.Of(_driver.Rank).ToUpper()}";
        BonusList.ItemsSource = c.ClassBonus.TryGetValue(_driver.Rank, out var bonuses)
            ? bonuses.Select(b => new LineRow(
                Names.Of(b.Key) + (PayCalculator.MissingPermit(_driver, b.Key) is { } p ? $"  (нет допуска «{p}»)" : ""),
                $"€{b.Value:N0}", null)).ToList()
            : new List<LineRow>();

        var other = new List<string>
        {
            $"Рейс в город гаража или из него: +{c.GarageCityBonusPercent:P0} стоимости груза ({string.Join(", ", c.GarageCities)})",
            $"Больше {c.ManyTripsPerDay} грузов за сутки: +{c.ManyTripsBonusPercent:P0} стоимости груза",
        };
        if (c.RankMassLimitTonnes.TryGetValue(_driver.Rank, out var limit))
            other.Add($"Для вашего ранга надбавка платится за грузы до {limit:0} т");
        other.Add("Классы «Важный» и «Срочный» игра не сообщает — выберите их у рейса вручную.");
        OtherRatesText.Text = string.Join("\n", other);
    }

    private void OnOpenData(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("explorer.exe", _s.DataDir) { UseShellExecute = true });

    // ─────────────────────────── Строки таблиц ───────────────────────────

    private sealed record StatRow(string Label, string Month, string Total);

    private sealed record LineRow(string Label, string Value, Brush? Brush)
    {
        private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));
        private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

        public static LineRow Points(string label, double v)
            => new(label, v.ToString("+0;-0;0"), v < 0 ? Bad : v > 0 ? Good : null);

        public static LineRow Money(string label, double v)
            => new(label, $"€{v:N0}", v > 0 ? Good : null);
    }

    private sealed record TripRow(TripResult Trip)
    {
        public string Date => Trip.FinishedAt.ToString("dd.MM HH:mm");
        public string Kind => Trip.Status == TripStatus.Cancelled ? "Отменён" : Names.Of(Trip.Kind);
        public string Cargo => Trip.CargoName;
        public string Class => Names.Of(Trip.CargoClass);
        public string Km => Trip.DistanceKm.ToString("N0");
        public string Score => Trip.Status == TripStatus.Delivered ? $"{Trip.Score} {Trip.Grade}" : Trip.Score.ToString();
        public string Salary => $"€{Trip.Salary:N0}";
    }
}
