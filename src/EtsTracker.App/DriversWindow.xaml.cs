using System.Windows;

namespace EtsTracker.App;

/// <summary>Список водителей для администратора: ранги, допуски, роли.</summary>
public partial class DriversWindow : Window
{
    private readonly AppServices _services;
    private readonly long _currentDriverId;

    /// <summary>Были ли изменения — окну трекера нужно перечитать водителя.</summary>
    public bool Changed { get; private set; }

    public DriversWindow(AppServices services, long currentDriverId)
    {
        InitializeComponent();
        _services = services;
        _currentDriverId = currentDriverId;
        Load(null);
    }

    private void Load(long? selectId)
    {
        var rows = _services.Repo.GetDrivers().Select(d => new Row(d)).ToList();
        DriversGrid.ItemsSource = rows;
        DriversGrid.SelectedItem = rows.FirstOrDefault(r => r.Driver.Id == selectId) ?? rows.FirstOrDefault();
        UpdateButtons();
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var row = DriversGrid.SelectedItem as Row;
        EditButton.IsEnabled = row != null;
        // Свой PIN меняют кнопкой «Сменить мой PIN», сбрасывают — чужой
        ResetPinButton.IsEnabled = row is { Driver: { IsAdmin: true, HasPin: true } } && row.Driver.Id != _currentDriverId;
    }

    private void OnChangeMyPin(object sender, RoutedEventArgs e)
    {
        var me = _services.Repo.GetDrivers().FirstOrDefault(d => d.Id == _currentDriverId);
        if (me == null || !PinWindow.Change(this, _services, me)) return;
        MessageBox.Show(this, "PIN изменён.", "PIN", MessageBoxButton.OK, MessageBoxImage.Information);
        Load(me.Id);
    }

    private void OnResetPin(object sender, RoutedEventArgs e)
    {
        if (DriversGrid.SelectedItem is not Row row) return;
        var answer = MessageBox.Show(this,
            $"Сбросить PIN администратора «{row.Driver.Nickname}»? При следующем входе он придумает новый.",
            "Сброс PIN", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _services.Repo.SetPin(row.Driver.Id, null);
        Load(row.Driver.Id);
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (DriversGrid.SelectedItem is not Row row) return;
        if (new DriverEditWindow(_services, row.Driver, canManage: true) { Owner = this }.ShowDialog() != true) return;
        Changed = true;
        Load(row.Driver.Id);
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        var edit = new DriverEditWindow(_services, null, canManage: true) { Owner = this };
        if (edit.ShowDialog() != true) return;
        Changed = true;
        Load(edit.Driver.Id);
    }

    private sealed record Row(Driver Driver)
    {
        public string RegNumber => Driver.RegNumber;
        public string Nickname => Driver.Nickname;
        public string Rank => Names.Of(Driver.Rank);
        public string Truck => Driver.Truck;
        public string Role => Driver.IsAdmin ? "★ Администратор" : "Водитель";
        public string PinState => !Driver.IsAdmin ? "" : Driver.HasPin ? "задан" : "нет";

        public string Permits
        {
            get
            {
                var list = new List<string>();
                if (Driver.PermitAdr) list.Add("ADR");
                if (Driver.PermitHeavy) list.Add("тяжёлые");
                if (Driver.PermitArticulated) list.Add("сочленённые");
                return list.Count > 0 ? string.Join(", ", list) : "—";
            }
        }
    }
}
