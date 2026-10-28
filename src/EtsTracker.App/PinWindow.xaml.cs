using System.Windows;

namespace EtsTracker.App;

/// <summary>Ввод PIN администратора при входе или придумывание нового PIN.</summary>
public partial class PinWindow : Window
{
    private readonly AppServices _services;
    private readonly Driver _driver;
    private readonly bool _create;
    private int _failures;

    private PinWindow(AppServices services, Driver driver, bool create)
    {
        InitializeComponent();
        _services = services;
        _driver = driver;
        _create = create;

        if (create)
        {
            HeaderText.Text = driver.HasPin ? "Новый PIN" : "Придумайте PIN";
            HintText.Text = $"PIN защищает вход под администратором «{driver.Nickname}». " +
                            $"От {Pin.MinLength} до {Pin.MaxLength} цифр.";
            PinLabel.Text = "Новый PIN";
            RepeatPanel.Visibility = Visibility.Visible;
            OkButton.Content = "Сохранить";
        }
        else
        {
            HeaderText.Text = $"Вход: {driver.Nickname}";
            HintText.Text = "Введите PIN администратора.";
        }
        Loaded += (_, _) => PinBox.Focus();
    }

    /// <summary>Спрашивает PIN при входе администратора. Если PIN ещё не задан — просит его придумать.</summary>
    public static bool Authorize(Window owner, AppServices services, Driver driver)
    {
        if (!driver.IsAdmin) return true;
        var window = new PinWindow(services, driver, create: !driver.HasPin) { Owner = owner };
        return window.ShowDialog() == true;
    }

    /// <summary>Смена своего PIN.</summary>
    public static bool Change(Window owner, AppServices services, Driver driver)
        => new PinWindow(services, driver, create: true) { Owner = owner }.ShowDialog() == true;

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        var pin = PinBox.Password;
        if (_create)
        {
            if (!Pin.IsValid(pin))
            {
                ShowError($"PIN — от {Pin.MinLength} до {Pin.MaxLength} цифр.");
                return;
            }
            if (pin != RepeatBox.Password)
            {
                ShowError("PIN-коды не совпадают.");
                return;
            }
            _services.Repo.SetPin(_driver.Id, pin);
            _driver.HasPin = true;
            DialogResult = true;
            return;
        }

        if (_services.Repo.CheckPin(_driver.Id, pin))
        {
            DialogResult = true;
            return;
        }

        // Неверный PIN: с каждой ошибкой ждём дольше, чтобы его нельзя было быстро подобрать
        _failures++;
        PinBox.Clear();
        OkButton.IsEnabled = false;
        ShowError(_failures >= 3 ? $"Неверный PIN. Подождите {_failures} с…" : "Неверный PIN.");
        await Task.Delay(TimeSpan.FromSeconds(_failures >= 3 ? _failures : 1));
        OkButton.IsEnabled = true;
        if (_failures >= 3) ShowError("Неверный PIN.");
        PinBox.Focus();
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }
}
