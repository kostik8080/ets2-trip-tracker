using System.Windows;

namespace EtsTracker.App;

/// <summary>Регистрация нового водителя или правка профиля.</summary>
public partial class DriverEditWindow : Window
{
    private readonly AppServices _services;
    private readonly bool _isNew;

    public Driver Driver { get; }

    public DriverEditWindow(AppServices services, Driver? driver)
    {
        InitializeComponent();
        _services = services;
        _isNew = driver == null;
        Driver = driver ?? new Driver { RegNumber = NextRegNumber(services.Repo.GetDrivers()) };

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

        Driver.Nickname = nickname;
        Driver.RegNumber = RegNumberBox.Text.Trim();
        Driver.Truck = TruckBox.Text.Trim();
        Driver.Rank = RankBox.SelectedValue is DriverRank r ? r : DriverRank.Trainee;
        Driver.PermitAdr = AdrBox.IsChecked == true;
        Driver.PermitHeavy = HeavyBox.IsChecked == true;
        Driver.PermitArticulated = ArticulatedBox.IsChecked == true;

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
