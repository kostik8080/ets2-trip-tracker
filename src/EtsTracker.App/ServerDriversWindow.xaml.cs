using System.Windows;
using System.Windows.Controls;

namespace EtsTracker.App;

/// <summary>Администратор меняет ранги, допуски и роли водителей на сервере.</summary>
public partial class ServerDriversWindow : Window
{
    private readonly ServerClient _client;
    private readonly Action<Window> _openLocalProfiles;

    /// <summary>Были ли изменения: рейтинг нужно перечитать.</summary>
    public bool Changed { get; private set; }

    public ServerDriversWindow(ServerClient client, Action<Window> openLocalProfiles)
    {
        InitializeComponent();
        _client = client;
        _openLocalProfiles = openLocalProfiles;
        RankBox.ItemsSource = Choices.Ranks;
        Loaded += async (_, _) => await LoadAsync(null);
    }

    private async Task LoadAsync(long? selectId)
    {
        try
        {
            var rows = (await _client.GetDriversAsync()).Select(d => new Row(d)).ToList();
            DriversGrid.ItemsSource = rows;
            DriversGrid.SelectedItem = rows.FirstOrDefault(r => r.D.Id == selectId) ?? rows.FirstOrDefault();
        }
        catch (ServerException ex)
        {
            ShowError(ex.Message);
        }
    }

    private void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        SaveButton.IsEnabled = DriversGrid.SelectedItem != null;
        if (DriversGrid.SelectedItem is not Row row) return;
        RankBox.SelectedValue = row.D.Rank;
        AdrBox.IsChecked = row.D.PermitAdr;
        HeavyBox.IsChecked = row.D.PermitHeavy;
        ArticulatedBox.IsChecked = row.D.PermitArticulated;
        AdminBox.IsChecked = row.D.IsAdmin;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (DriversGrid.SelectedItem is not Row row) return;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            await _client.UpdateDriverAsync(row.D.Id, new DriverUpdate(
                RankBox.SelectedValue is DriverRank r ? r : row.D.Rank,
                AdrBox.IsChecked == true, HeavyBox.IsChecked == true, ArticulatedBox.IsChecked == true,
                AdminBox.IsChecked == true));
            Changed = true;
            await LoadAsync(row.D.Id);
        }
        catch (ServerException ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnLocalProfiles(object sender, RoutedEventArgs e) => _openLocalProfiles(this);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private sealed record Row(ServerDriverInfo D)
    {
        public string Nickname => D.Nickname;
        public string Rank => D.RankName;
        public string Role => D.IsAdmin ? "★ Администратор" : "Водитель";

        public string Permits
        {
            get
            {
                var list = new List<string>();
                if (D.PermitAdr) list.Add("ADR");
                if (D.PermitHeavy) list.Add("тяжёлые");
                if (D.PermitArticulated) list.Add("сочленённые");
                return list.Count > 0 ? string.Join(", ", list) : "—";
            }
        }
    }
}
