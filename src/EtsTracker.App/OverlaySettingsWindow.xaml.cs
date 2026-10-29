using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EtsTracker.App;

/// <summary>Настройки оверлея: изменения применяются сразу и сохраняются в data/overlay.json.</summary>
public partial class OverlaySettingsWindow : Window
{
    private static readonly string[] Palette =
        { "#FFFFFF", "#FFE066", "#7CFC9A", "#7FDBFF", "#FFB347", "#B9C7FF", "#FF8A8A", "#111827" };

    private readonly AppServices _s;
    private readonly OverlayWindow _overlay;
    private readonly Action _refresh;
    private bool _loading = true;
    private bool _moving;

    public OverlaySettingsWindow(AppServices services, OverlayWindow overlay, Action refresh)
    {
        InitializeComponent();
        _s = services;
        _overlay = overlay;
        _refresh = refresh;

        var o = _s.Overlay;
        EnabledBox.IsChecked = o.Enabled;
        IdleBox.IsChecked = o.ShowWithoutJob;
        RealTimeBox.IsChecked = o.TimeMode == TimeMode.Real;
        GameTimeBox.IsChecked = o.TimeMode == TimeMode.Game;
        OpacitySlider.Value = Math.Round(o.BackgroundOpacity * 100);
        SizeSlider.Value = o.FontSize;
        ColorBox.Text = o.TextColor;

        foreach (var hex in Palette)
        {
            var swatch = new Button
            {
                Width = 26, Height = 26, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                ToolTip = hex, Tag = hex,
            };
            swatch.Click += (_, _) => ColorBox.Text = (string)swatch.Tag;
            Swatches.Children.Add(swatch);
        }

        _loading = false;
        UpdateLabels();
        _overlay.Preview = true;
        _refresh();
        Closed += (_, _) =>
        {
            if (_moving) _overlay.SetMoveMode(false);
            _overlay.Preview = false;
            _s.SaveOverlay();
            _refresh();
        };
    }

    private void OnChanged(object sender, RoutedEventArgs e) => Apply();

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Apply();

    private void OnColorTyped(object sender, TextChangedEventArgs e)
    {
        if (TryColor(ColorBox.Text.Trim(), out _)) Apply();
        else UpdateLabels();
    }

    private void Apply()
    {
        if (_loading) return;
        var o = _s.Overlay;
        o.Enabled = EnabledBox.IsChecked == true;
        o.ShowWithoutJob = IdleBox.IsChecked == true;
        o.TimeMode = GameTimeBox.IsChecked == true ? TimeMode.Game : TimeMode.Real;
        o.BackgroundOpacity = OpacitySlider.Value / 100;
        o.FontSize = SizeSlider.Value;
        if (TryColor(ColorBox.Text.Trim(), out _)) o.TextColor = ColorBox.Text.Trim();

        UpdateLabels();
        _overlay.ApplyLook();
        _s.SaveOverlay();
        _refresh();
    }

    private void UpdateLabels()
    {
        OpacityValue.Text = OpacitySlider.Value == 0 ? "без фона" : $"{OpacitySlider.Value:0} %";
        SizeValue.Text = $"{SizeSlider.Value:0}";
        ColorPreview.Background = TryColor(ColorBox.Text.Trim(), out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
    }

    private void OnMove(object sender, RoutedEventArgs e)
    {
        _moving = !_moving;
        _overlay.SetMoveMode(_moving);
        MoveButton.Content = _moving ? "Готово" : "Переместить оверлей";
        _refresh();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static bool TryColor(string text, out Color color)
    {
        color = Colors.White;
        if (!text.StartsWith('#')) return false;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(text);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            return false;
        }
    }
}
