using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace EtsTracker.App;

/// <summary>Название компании и логотип в шапке.</summary>
public partial class CompanyWindow : Window
{
    private readonly AppServices _services;
    private string? _newLogoPath;
    private bool _resetLogo;

    public CompanyWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        NameBox.Text = services.Company.CompanyName;
        LogoPreview.Source = services.LoadLogo();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnPickLogo(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Логотип компании",
            Filter = "Картинки (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(dialog.FileName);
            img.EndInit();
            LogoPreview.Source = img;
            _newLogoPath = dialog.FileName;
            _resetLogo = false;
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            ErrorText.Text = "Не удалось открыть эту картинку.";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void OnResetLogo(object sender, RoutedEventArgs e)
    {
        _newLogoPath = null;
        _resetLogo = true;
        LogoPreview.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png"));
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ErrorText.Text = "Введите название компании.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        _services.SaveCompanyLook(name, _newLogoPath, _resetLogo);
        DialogResult = true;
    }
}
