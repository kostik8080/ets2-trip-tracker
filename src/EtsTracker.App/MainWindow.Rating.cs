using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace EtsTracker.App;

/// <summary>Вкладка «Рейтинг»: вход на сервер компании, отправка рейсов, общий рейтинг.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _syncing;
    private DateTime _lastLivePost;
    private bool _livePublished;
    private long? _ratingDriverId;

    private ServerAccount? Account => _s.Server.Accounts.GetValueOrDefault(_driver.Id);

    private void InitRating()
    {
        RankInviteBox.ItemsSource = Choices.Ranks;
        RankInviteBox.SelectedValue = DriverRank.Trainee;
        RatingPeriodBox.SelectedIndex = 1;
        RatingMetricBox.SelectedIndex = 0;
        _syncTimer.Tick += async (_, _) =>
        {
            await SyncAsync();
            await LoadLeaderboardAsync();
        };
        _syncTimer.Start();
        _liveTimer.Tick += async (_, _) => await LoadLiveAsync();
        _liveTimer.Start();
        Closed += (_, _) =>
        {
            _syncTimer.Stop();
            _liveTimer.Stop();
        };
    }

    /// <summary>Показывает форму входа или рейтинг — в зависимости от выбранного водителя.</summary>
    private async Task ShowRatingAsync()
    {
        var account = Account;
        ServerConnectPanel.Visibility = account == null ? Visibility.Visible : Visibility.Collapsed;
        ServerRatingPanel.Visibility = account == null ? Visibility.Collapsed : Visibility.Visible;
        ServerErrorText.Visibility = Visibility.Collapsed;

        if (account == null)
        {
            ServerUrlBox.Text = _s.Server.Url;
            ServerNicknameBox.Text = _driver.Nickname;
            ServerPasswordBox.Clear();
            ServerInviteBox.Clear();
            return;
        }

        // Вход сделан до появления этой отметки: рейсы отправлены на текущий сервер
        if (_s.Server.SyncedTo.TryAdd(_driver.Id, _s.Server.Url)) _s.SaveServer();
        ServerAccountText.Text = $"Вы вошли как {account.Nickname} · {_s.Server.Url}";
        InvitePanel.Visibility = account.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        InviteCodeBox.Visibility = Visibility.Collapsed;
        await SyncAsync();
        await LoadLeaderboardAsync();
        await LoadLiveAsync();
    }

    // ─────────────────────────────── Вход ───────────────────────────────

    private void ShowServerError(string text)
    {
        ServerErrorText.Text = text;
        ServerErrorText.Visibility = Visibility.Visible;
    }

    private async void OnServerLogin(object sender, RoutedEventArgs e)
        => await ConnectAsync(c => c.LoginAsync(new LoginRequest(ServerNicknameBox.Text.Trim(), ServerPasswordBox.Password)));

    private async void OnServerRegister(object sender, RoutedEventArgs e)
        => await ConnectAsync(c => c.RegisterAsync(new RegisterRequest(
            ServerInviteBox.Text.Trim(), ServerNicknameBox.Text.Trim(), ServerPasswordBox.Password,
            // Сервер примет ранг и допуски только от первого администратора
            _driver.Rank, _driver.PermitAdr, _driver.PermitHeavy, _driver.PermitArticulated)));

    private async Task ConnectAsync(Func<ServerClient, Task<AuthResponse>> connect)
    {
        ServerConnectPanel.IsEnabled = false;
        ServerErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var url = ServerUrlBox.Text.Trim();
            var auth = await connect(new ServerClient(url));
            _s.Server.Url = ServerClient.ParseUrl(url).ToString().TrimEnd('/');
            if (_s.Server.SyncedTo.TryGetValue(_driver.Id, out var previous) && previous != _s.Server.Url)
                _s.Repo.ResetSynced(_driver.Id);
            _s.Server.SyncedTo[_driver.Id] = _s.Server.Url;
            _s.Server.Accounts[_driver.Id] = new ServerAccount
            {
                Nickname = auth.Driver.Nickname, Token = auth.Token, IsAdmin = auth.Driver.IsAdmin,
            };
            _s.SaveServer();
            AddNotice($"🌐 Вход на сервер выполнен: {auth.Driver.Nickname}");
            AskAboutOldTrips();
            await ShowRatingAsync();
        }
        catch (ServerException ex)
        {
            ShowServerError(ex.Message);
        }
        finally
        {
            ServerConnectPanel.IsEnabled = true;
        }
    }

    /// <summary>Рейсы, записанные до входа: отправлять их или нет, решает сам водитель.</summary>
    private void AskAboutOldTrips()
    {
        var old = _s.Repo.GetTrips(_driver.Id, unsyncedOnly: true);
        if (old.Count == 0) return;
        var answer = MessageBox.Show(this,
            $"У этого водителя записано рейсов: {old.Count}. Отправить их на сервер, чтобы они вошли в рейтинг?\n\n" +
            "Если нет, в рейтинг пойдут только новые рейсы.",
            "Рейсы до входа", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            _s.Repo.MarkSynced(old.Select(t => t.ClientId), "skipped");
    }

    private async void OnServerLogout(object sender, RoutedEventArgs e)
    {
        if (Account is { } account)
            await new ServerClient(_s.Server.Url, account.Token).LogoutAsync();
        _s.Server.Accounts.Remove(_driver.Id);
        _s.SaveServer();
        await ShowRatingAsync();
    }

    /// <summary>Токен больше не действует: просим войти заново, рейсы дождутся в очереди.</summary>
    private async Task HandleLoggedOutAsync()
    {
        _s.Server.Accounts.Remove(_driver.Id);
        _s.SaveServer();
        await ShowRatingAsync();
        ShowServerError("Вход на сервер устарел. Войдите заново: неотправленные рейсы сохранены.");
    }

    // ───────────────────────── Отправка рейсов ─────────────────────────

    /// <summary>Отправляет всё, что ещё не на сервере. Без связи рейсы просто ждут следующей попытки.</summary>
    private async Task SyncAsync()
    {
        if (_syncing || Account is not { } account) return;
        _syncing = true;
        try
        {
            var client = new ServerClient(_s.Server.Url, account.Token);
            var pending = _s.Repo.GetTrips(_driver.Id, unsyncedOnly: true);
            pending.Reverse(); // сначала старые: от порядка зависит зарплата за сутки

            foreach (var batch in pending.Chunk(100))
            {
                var result = await client.UploadAsync(batch.Select(TripUpload.FromTrip).ToList());
                _s.Repo.MarkSynced(result.Saved);
                // Отклонённые не отправляем снова: они не пройдут и в следующий раз
                _s.Repo.MarkSynced(result.Rejected.Select(r => r.ClientId), "rejected");
                foreach (var r in result.Rejected) AddNotice($"⚠ Сервер не принял рейс: {r.Error}");
            }
            SyncStatusText.Text = pending.Count > 0 ? $"Отправлено рейсов: {pending.Count}" : "Все рейсы на сервере";
        }
        catch (ServerException ex) when (ex.Unauthorized)
        {
            await HandleLoggedOutAsync();
        }
        catch (ServerException ex)
        {
            int waiting = _s.Repo.GetTrips(_driver.Id, unsyncedOnly: true).Count;
            SyncStatusText.Text = waiting > 0 ? $"{ex.Message} Ждут отправки: {waiting}" : ex.Message;
        }
        finally
        {
            _syncing = false;
        }
    }

    // ──────────────────────────── Сейчас в рейсе ───────────────────────────

    /// <summary>Раз в 15 секунд сообщает серверу о текущем рейсе; демо-рейс не показывается другим.</summary>
    private async void PublishLive(ActiveTrip? trip, TelemetryFrame? f)
    {
        if (_demoTracker != null || Account is not { } account) return;
        try
        {
            var client = new ServerClient(_s.Server.Url, account.Token);
            if (trip == null || f == null)
            {
                if (!_livePublished) return;
                _livePublished = false;
                await client.ClearLiveAsync();
                return;
            }
            if (DateTime.Now - _lastLivePost < TimeSpan.FromSeconds(15)) return;
            _lastLivePost = DateTime.Now;
            _livePublished = true;
            await client.SendLiveAsync(new LiveTripUpdate
            {
                CargoName = trip.CargoName,
                CargoMassT = trip.CargoMassKg / 1000,
                From = TimeView.City(trip.From),
                To = TimeView.City(trip.To),
                CargoLoaded = trip.CargoLoaded,
                Progress = trip.Progress,
                RemainingKm = trip.RemainingKm,
                SpeedKph = f.SpeedKph,
                Paused = f.Paused,
            });
        }
        catch (ServerException) { /* нет связи — сообщим в следующий раз */ }
    }

    private async Task LoadLiveAsync()
    {
        if (Account is not { } account) return;
        try
        {
            var list = await new ServerClient(_s.Server.Url, account.Token).GetLiveAsync();
            LiveList.ItemsSource = list.Select(l => new LiveRow(l)).ToList();
            LiveEmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (ServerException) { }
    }

    private sealed record LiveRow(LiveTrip L)
    {
        public string Nickname => $"{L.Nickname} · {L.RankName}";
        public string Cargo => $"{L.Trip.CargoName}, {L.Trip.CargoMassT:0.#} т";
        public string Route => $"{L.Trip.From} → {L.Trip.To}";
        public double Percent => L.Trip.Progress * 100;
        public string Status
        {
            get
            {
                var where = L.Trip.CargoLoaded ? $"осталось {L.Trip.RemainingKm:N0} км · {L.Trip.Progress:P0}" : "едет за грузом";
                return $"{where} · {(L.Trip.Paused ? "пауза" : $"{L.Trip.SpeedKph:0} км/ч")}";
            }
        }
    }

    // ───────────────────────────── Рейтинг ─────────────────────────────

    private async Task LoadLeaderboardAsync()
    {
        if (Account is not { } account) return;
        try
        {
            var period = ((ComboBoxItem)RatingPeriodBox.SelectedItem).Tag as string ?? "month";
            var metric = ((ComboBoxItem)RatingMetricBox.SelectedItem).Tag as string ?? "score";
            var board = await new ServerClient(_s.Server.Url, account.Token).GetLeaderboardAsync(period, metric);

            var rows = board.Rows
                .Select(r => new RatingRow(r, board.AchievementsTotal, r.Nickname.Equals(account.Nickname, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var keep = _ratingDriverId;
            RatingGrid.ItemsSource = rows;
            RatingGrid.SelectedItem = rows.FirstOrDefault(r => r.R.DriverId == keep);
            RatingTotalsText.Text = $"Общий счёт {board.CompanyName}: €{board.CompanyAccount:N0} · " +
                                    $"за период {board.TotalDelivered:N0} рейсов · {board.TotalKm:N0} км · " +
                                    $"{board.TotalTonnes:N0} т · зарплата €{board.TotalSalary:N0}";
        }
        catch (ServerException ex) when (!ex.Unauthorized)
        {
            RatingTotalsText.Text = ex.Message;
        }
    }

    private async void OnRatingDriverSelected(object sender, SelectionChangedEventArgs e)
    {
        if (RatingGrid.SelectedItem is not RatingRow row || Account is not { } account) return;
        _ratingDriverId = row.R.DriverId;
        try
        {
            var a = await new ServerClient(_s.Server.Url, account.Token).GetDriverAchievementsAsync(row.R.DriverId);
            var got = a.Items.Where(i => i.Unlocked).ToList();
            RatingAchTitle.Text = got.Count == 0
                ? $"{a.Nickname}: пока нет достижений"
                : $"Достижения {a.Nickname}: {a.Unlocked} из {a.Total}";
            RatingAchList.ItemsSource = got.Select(i => new RatingAchRow(i)).ToList();
        }
        catch (ServerException ex)
        {
            RatingAchTitle.Text = ex.Message;
        }
    }

    private sealed record RatingAchRow(AchievementInfo I)
    {
        public string Icon => I.Icon;
        public string Title => I.Title;
        public string Tip => $"{I.Description} Получено {I.UnlockedAt?.ToLocalTime():dd.MM.yyyy}";
    }

    private async void OnRatingFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await LoadLeaderboardAsync();
    }

    private async void OnRatingRefresh(object sender, RoutedEventArgs e)
    {
        await SyncAsync();
        await LoadLeaderboardAsync();
    }

    private void OnOpenRatingPage(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(ServerClient.ParseUrl(_s.Server.Url).ToString()) { UseShellExecute = true });

    private async void OnCreateInvite(object sender, RoutedEventArgs e)
    {
        if (Account is not { IsAdmin: true } account) return;
        try
        {
            var rank = RankInviteBox.SelectedValue is DriverRank r ? r : DriverRank.Trainee;
            var invite = await new ServerClient(_s.Server.Url, account.Token).CreateInviteAsync(rank);
            InviteCodeBox.Text = invite.Code;
            InviteCodeBox.Visibility = Visibility.Visible;
        }
        catch (ServerException ex)
        {
            AddNotice($"⚠ {ex.Message}");
        }
    }

    private sealed record RatingRow(LeaderboardRow R, int AchievementsTotal, bool IsMe)
    {
        private static readonly Brush Gold = new SolidColorBrush(Color.FromRgb(0xE3, 0xB5, 0x2B));
        private static readonly Brush Silver = new SolidColorBrush(Color.FromRgb(0xB4, 0xBC, 0xC6));
        private static readonly Brush Bronze = new SolidColorBrush(Color.FromRgb(0xCD, 0x85, 0x45));

        public string Place => R.Place.ToString();
        public Brush MedalBrush => R.Place switch { 1 => Gold, 2 => Silver, 3 => Bronze, _ => Brushes.Transparent };
        public Brush PlaceBrush => R.Place <= 3 ? Brushes.White : Brushes.Black;
        public string Nickname => (R.IsAdmin ? $"{R.Nickname} ★" : R.Nickname) + (IsMe ? "  (вы)" : "");
        public string Rank => R.RankName;
        public string Score => R.Score.ToString("N0");
        public string Km => R.Km.ToString("N0");
        public string Salary => $"€{R.Salary:N0}";
        public string Delivered => R.Delivered.ToString();
        public string Perfect => R.Perfect.ToString();
        public string Fines => R.Fines.ToString();
        public string Level => R.Level.ToString();
        public string Achievements => $"{R.Achievements} / {AchievementsTotal}";
    }
}
