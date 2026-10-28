using System.Windows;

namespace EtsTracker.App;

/// <summary>Список водителей для администратора: ранги, допуски, роли.</summary>
public partial class DriversWindow : Window
{
    private readonly AppServices _services;

    /// <summary>Были ли изменения — окну трекера нужно перечитать водителя.</summary>
    public bool Changed { get; private set; }

    public DriversWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        Load(null);
    }

    private void Load(long? selectId)
    {
        var rows = _services.Repo.GetDrivers().Select(d => new Row(d)).ToList();
        DriversGrid.ItemsSource = rows;
        DriversGrid.SelectedItem = rows.FirstOrDefault(r => r.Driver.Id == selectId) ?? rows.FirstOrDefault();
        EditButton.IsEnabled = DriversGrid.SelectedItem != null;
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e)
        => EditButton.IsEnabled = DriversGrid.SelectedItem != null;

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
