using System.Windows;

namespace EtsTracker.App;

/// <summary>
/// Регистрация нового водителя или правка профиля.
/// Ранг, допуски и роль администратора меняет только администратор (canManage).
/// Водитель получает их с сервера компании после входа на вкладке «Рейтинг».
/// </summary>
public partial class DriverEditWindow : Window
{
    private readonly AppServices _services;
    private readonly bool _isNew;
    private readonly bool _canManage;

    public Driver Driver { get; }

    public DriverEditWindow(AppServices services, Driver? driver, bool canManage)
    {
        InitializeComponent();
        _services = services;
        _isNew = driver == null;
        _canManage = canManage;
        var drivers = services.Repo.GetDrivers();
        Driver = driver ?? new Driver { RegNumber = NextRegNumber(drivers) };

        HeaderText.Text = _isNew ? "Регистрация водителя" : "Профиль водителя";
        TruckBox.ItemsSource = services.Company.Trucks;
        RankBox.ItemsSource = Choices.Ranks;

        NicknameBox.Text = Driver.Nickname;
        RegNumberBox.Text = Driver.RegNumber;
        TruckBox.Text = Driver.Truck;
        RankBox.SelectedValue = Driver.Rank;
        AdrBox.IsChecked = Driver.PermitAdr;
        HeavyBox.IsChecked = Driver.PermitHeavy;
        ArticulatedBox.IsChecked = Driver.PermitArticulated;
        AdminBox.IsChecked = Driver.IsAdmin;

        RankBox.IsEnabled = AdrBox.IsEnabled = HeavyBox.IsEnabled = ArticulatedBox.IsEnabled = AdminBox.IsEnabled = canManage;
        if (!canManage)
        {
            ManageHint.Text = "Ранг, допуски и роль назначает администратор компании. Они появятся здесь после входа на сервер на вкладке «Рейтинг».";
        }
        ManageHint.Visibility = string.IsNullOrEmpty(ManageHint.Text) ? Visibility.Collapsed : Visibility.Visible;

        Loaded += (_, _) => NicknameBox.Focus();
    }

    /// <summary>Следующий свободный номер: 01, 02, 03…</summary>
    private static string NextRegNumber(List<Driver> drivers)
    {
        int max = drivers.Select(d => int.TryParse(d.RegNumber, out var n) ? n : 0).DefaultIfEmpty(0).Max();
        return (max + 1).ToString("00");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var nickname = NicknameBox.Text.Trim();
        if (nickname.Length == 0)
        {
            ShowError("Введите никнейм.");
            return;
        }
        if (_services.Repo.NicknameTaken(nickname, Driver.Id))
        {
            ShowError($"Водитель «{nickname}» уже зарегистрирован.");
            return;
        }

        bool admin = AdminBox.IsChecked == true;
        if (!_isNew && Driver.IsAdmin && !admin && _services.Repo.OtherAdminsCount(Driver.Id) == 0)
        {
            ShowError("Это единственный администратор. Сначала назначьте администратором другого водителя.");
            return;
        }

        Driver.Nickname = nickname;
        Driver.RegNumber = RegNumberBox.Text.Trim();
        Driver.Truck = TruckBox.Text.Trim();
        if (_canManage)
        {
            Driver.Rank = RankBox.SelectedValue is DriverRank r ? r : DriverRank.Trainee;
            Driver.PermitAdr = AdrBox.IsChecked == true;
            Driver.PermitHeavy = HeavyBox.IsChecked == true;
            Driver.PermitArticulated = ArticulatedBox.IsChecked == true;
            Driver.IsAdmin = admin;
        }

        if (_isNew)
        {
            _services.Repo.CreateDriver(Driver);
            _services.Career.PayUnpaidTrips(Driver);
        }
        else _services.Repo.UpdateDriver(Driver);
        DialogResult = true;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }
}
