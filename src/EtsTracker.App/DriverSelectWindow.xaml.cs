using System.Windows;

namespace EtsTracker.App;

public partial class DriverSelectWindow : Window
{
    private readonly AppServices _services;

    public Driver? Chosen { get; private set; }

    public DriverSelectWindow(AppServices services, long? selectId = null)
    {
        InitializeComponent();
        _services = services;
        CompanyText.Text = services.Company.CompanyName;
        LogoImage.Source = services.LoadLogo();
        Title = $"{services.Company.CompanyName} — выбор водителя";
        Load(selectId ?? services.State.LastDriverId);
    }

    private void Load(long? selectId)
    {
        var rows = _services.Repo.GetDrivers().Select(d => new DriverRow(d)).ToList();
        DriversList.ItemsSource = rows;
        DriversList.SelectedItem = rows.FirstOrDefault(r => r.Driver.Id == selectId) ?? rows.FirstOrDefault();
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EnterButton.IsEnabled = DriversList.SelectedItem != null;
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e)
        => EnterButton.IsEnabled = DriversList.SelectedItem != null;

    private void OnNew(object sender, RoutedEventArgs e)
    {
        // Самый первый водитель регистрируется сам; остальных назначает администратор
        bool first = _services.Repo.GetDrivers().Count == 0;
        var edit = new DriverEditWindow(_services, null, canManage: first) { Owner = this };
        if (edit.ShowDialog() == true)
            Load(edit.Driver.Id);
    }

    private void OnEnter(object sender, RoutedEventArgs e)
    {
        if (DriversList.SelectedItem is not DriverRow row) return;
        Chosen = row.Driver;
        _services.RememberDriver(row.Driver);
        DialogResult = true;
    }

    private sealed record DriverRow(Driver Driver)
    {
        public string Nickname => Driver.Nickname;
        public string RegNumber => Driver.RegNumber;
        public string Truck => string.IsNullOrEmpty(Driver.Truck) ? "грузовик не указан" : Driver.Truck;
        public string RankName => Driver.IsAdmin ? $"{Names.Of(Driver.Rank)} · ★ администратор" : Names.Of(Driver.Rank);
    }
}
