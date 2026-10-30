using System.Windows;
using System.Windows.Controls;

namespace EtsTracker.App;

/// <summary>
/// Рейсы на сервере: администратор принимает и отклоняет рейсы водителей,
/// водитель смотрит свои (что принято, а что отклонено и почему).
/// </summary>
public partial class ServerTripsWindow : Window
{
    private readonly ServerClient _client;
    private readonly bool _review;

    /// <summary>Были ли решения по рейсам: рейтинг нужно перечитать.</summary>
    public bool Changed { get; private set; }

    public ServerTripsWindow(ServerClient client, bool review)
    {
        InitializeComponent();
        _client = client;
        _review = review;

        HeaderText.Text = review ? "Проверка рейсов" : "Мои рейсы на сервере";
        HintText.Text = review
            ? "В рейтинг идут только принятые рейсы. «Важный» и «Срочный» игра не сообщает: поправьте класс груза перед приёмом, зарплата пересчитается."
            : "В рейтинг идут только рейсы, принятые администратором.";
        FilterBox.Visibility = review ? Visibility.Visible : Visibility.Collapsed;
        ReviewPanel.Visibility = review ? Visibility.Visible : Visibility.Collapsed;
        KindBox.ItemsSource = Choices.TripKinds;
        ClassBox.ItemsSource = Choices.CargoClasses;

        if (review) FilterBox.SelectedIndex = 0; // загрузка — в OnFilterChanged
        else Loaded += async (_, _) => await LoadAsync();
    }

    private async void OnFilterChanged(object sender, SelectionChangedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            List<ServerTrip> trips;
            if (_review)
            {
                var approval = Enum.Parse<TripApproval>((string)((ComboBoxItem)FilterBox.SelectedItem).Tag);
                trips = await _client.GetTripsAsync(approval);
                EmptyText.Text = approval == TripApproval.Pending ? "Все рейсы проверены." : "Таких рейсов нет.";
            }
            else
            {
                trips = await _client.GetMyTripsAsync();
                EmptyText.Text = "На сервере пока нет ваших рейсов.";
            }
            TripsGrid.ItemsSource = trips.Select(t => new Row(t)).ToList();
            EmptyText.Visibility = trips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (trips.Count > 0) TripsGrid.SelectedIndex = 0;
            UpdateButtons();
        }
        catch (ServerException ex)
        {
            ShowError(ex.Message);
        }
    }

    private void OnTripSelected(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
        if (TripsGrid.SelectedItem is not Row row) return;
        KindBox.SelectedValue = row.T.Kind;
        ClassBox.SelectedValue = row.T.CargoClass;
        NoteBox.Text = row.T.ReviewNote;
        if (ErrorText != null) ErrorText.Visibility = Visibility.Collapsed;
    }

    private void UpdateButtons()
        => ApproveButton.IsEnabled = RejectButton.IsEnabled = TripsGrid.SelectedItem != null;

    private async void OnApprove(object sender, RoutedEventArgs e) => await ReviewAsync(approve: true);

    private async void OnReject(object sender, RoutedEventArgs e) => await ReviewAsync(approve: false);

    private async Task ReviewAsync(bool approve)
    {
        if (TripsGrid.SelectedItem is not Row row) return;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            await _client.ReviewTripAsync(row.T.Id, new TripReview(approve,
                KindBox.SelectedValue as TripKind?, ClassBox.SelectedValue as CargoClass?, NoteBox.Text));
            Changed = true;
            await LoadAsync();
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
        if (!_review) MessageBox.Show(this, text, "Рейсы на сервере", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private sealed record Row(ServerTrip T)
    {
        public string Date => T.FinishedAt.ToLocalTime().ToString("dd.MM HH:mm");
        public string Nickname => T.Nickname;
        public string State => T.Approval switch
        {
            TripApproval.Approved => "✔ Принят",
            TripApproval.Rejected => string.IsNullOrEmpty(T.ReviewNote) ? "✘ Отклонён" : $"✘ {T.ReviewNote}",
            _ => "⏳ На проверке",
        };
        public string Cargo => T.Status == TripStatus.Cancelled
            ? $"{T.CargoName} (отменён)"
            : $"{T.CargoName}, {T.CargoMassT:0.#} т";
        public string Route => $"{TimeView.City(T.From)} → {TimeView.City(T.To)}";
        public string Km => T.DistanceKm.ToString("N0");
        public string Kind => Names.Of(T.Kind);
        public string Class => Names.Of(T.CargoClass);
        public string Score => T.Score.ToString("N0");
        public string Salary => $"€{T.Salary:N0}";
    }
}
