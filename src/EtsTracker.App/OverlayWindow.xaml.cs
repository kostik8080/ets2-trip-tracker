using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Documents;
using System.Windows.Media;

namespace EtsTracker.App;

/// <summary>
/// Панель поверх игры: ход рейса, остаток пути, время, скорость, урон.
/// Прозрачна для мыши (клики проходят в игру) и не забирает фокус.
/// Видна поверх игры в оконном режиме или в режиме «на весь экран без рамки».
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly AppServices _s;
    private IntPtr _hwnd;
    private bool _moveMode;
    private DateTime _lastTopmost;

    private DateTime _toastUntil;

    /// <summary>Показать сообщение сверху оверлея на 10 секунд (новое достижение).</summary>
    public void Toast(string text)
    {
        ToastText.Text = text;
        _toastUntil = DateTime.Now.AddSeconds(10);
    }

    /// <summary>Показывать пример данных (пока открыты настройки и игра не запущена).</summary>
    public bool Preview { get; set; }

    public OverlayWindow(AppServices services)
    {
        InitializeComponent();
        _s = services;
        Left = _s.Overlay.Left;
        Top = _s.Overlay.Top;
        ApplyLook();
        MouseLeftButtonDown += (_, e) => { if (_moveMode && e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        LocationChanged += (_, _) =>
        {
            if (!_moveMode) return;
            _s.Overlay.Left = Left;
            _s.Overlay.Top = Top;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        SetClickThrough(true);
    }

    /// <summary>Применяет цвет, размер текста и прозрачность фона из настроек.</summary>
    public void ApplyLook()
    {
        var o = _s.Overlay;
        var text = ParseColor(o.TextColor);
        var brush = new SolidColorBrush(text);
        Panel.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(o.BackgroundOpacity, 0, 1) * 255), 15, 20, 35));
        TextElement.SetForeground(Root, brush);
        TextElement.SetFontSize(Root, o.FontSize);
        ClockText.FontSize = SpeedText.FontSize = ToastText.FontSize = o.FontSize * 1.15;
        CargoText.FontSize = o.FontSize * 1.1;
        BarTrack.Background = BarFill.Background = brush;
        BarTrack.Height = BarFill.Height = Math.Max(5, o.FontSize * 0.5);
        Root.Width = o.FontSize * 22;
    }

    /// <summary>Режим перемещения: оверлей ловит мышь, его можно перетащить.</summary>
    public void SetMoveMode(bool on)
    {
        _moveMode = on;
        SetClickThrough(!on);
        Panel.BorderBrush = on ? new SolidColorBrush(Color.FromRgb(0x6F, 0x86, 0xD6)) : Brushes.Transparent;
        MoveHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) _s.SaveOverlay();
    }

    /// <summary>Обновляет содержимое и решает, показывать ли оверлей.</summary>
    public void Update(bool live, TelemetryFrame? f, ActiveTrip? trip)
    {
        var o = _s.Overlay;
        bool preview = Preview && !live;
        bool show = _moveMode || preview || (o.Enabled && live && !f!.Paused && (trip != null || o.ShowWithoutJob));
        if (!show)
        {
            if (IsVisible) Hide();
            return;
        }

        if (preview || (_moveMode && !live)) FillSample();
        else Fill(f!, trip);
        ToastText.Visibility = DateTime.Now < _toastUntil ? Visibility.Visible : Visibility.Collapsed;

        if (!IsVisible) Show();
        KeepOnTop();
    }

    private void Fill(TelemetryFrame f, ActiveTrip? t)
    {
        var time = new TimeView(_s.Overlay.TimeMode, f);
        ClockText.Text = $"🕒 {time.Clock()}";
        SetSpeed(f.SpeedKph, f.SpeedLimitKph);

        if (t == null)
        {
            TripPanel.Visibility = Visibility.Collapsed;
            IdleText.Visibility = Visibility.Visible;
            IdleText.Text = "Без заказа";
            return;
        }
        TripPanel.Visibility = Visibility.Visible;
        IdleText.Visibility = Visibility.Collapsed;

        CargoText.Text = $"🚚 {t.CargoName} · {t.CargoMassKg / 1000:0.#} т";
        RouteText.Text = $"{TimeView.City(t.From)} → {TimeView.City(t.To)}";

        if (!t.CargoLoaded)
        {
            SetBar(0);
            RemainingText.Text = t.NavRemainingKm is { } toCargo ? $"До погрузки {toCargo:N0} км" : "Едем за грузом";
        }
        else
        {
            SetBar(t.Progress);
            RemainingText.Text = $"Осталось {t.RemainingKm:N0} км · {t.Progress:P0}";
        }

        SetLine(EtaText, t.NavEtaSec is { } eta
            ? $"В пути ≈ {time.Duration(eta / 60)} · прибытие {time.ClockAfter(eta / 60)}"
            : null);

        if (t.DeadlineMinutesLeft is { } left)
        {
            SetLine(DeadlineText, left >= 0 ? $"До срока {time.Duration(left)}" : $"⚠ Опоздание {time.Duration(-left)}");
            SetWarning(DeadlineText, left < 0);
        }
        else SetLine(DeadlineText, null);

        DamageText.Text = $"Урон: груз {f.CargoDamage:P1} · тягач {t.TruckDamage:P1} · прицеп {t.TrailerDamage:P1}";
    }

    /// <summary>Пример данных, чтобы настроить вид без запущенной игры.</summary>
    private void FillSample()
    {
        var time = new TimeView(_s.Overlay.TimeMode, new TelemetryFrame { GameTimeMinutes = 14 * 60 + 35 });
        ClockText.Text = $"🕒 {time.Clock()}";
        SetSpeed(85, 80);
        TripPanel.Visibility = Visibility.Visible;
        IdleText.Visibility = Visibility.Collapsed;
        CargoText.Text = "🚚 Стекло · 18,2 т";
        RouteText.Text = "Хельсинки → Турку";
        SetBar(0.49);
        RemainingText.Text = "Осталось 216 км · 49 %";
        SetLine(EtaText, $"В пути ≈ {time.Duration(153)} · прибытие {time.ClockAfter(153)}");
        SetLine(DeadlineText, $"До срока {time.Duration(337)}");
        SetWarning(DeadlineText, false);
        DamageText.Text = "Урон: груз 0,6 % · тягач 1,5 % · прицеп 0,7 %";
    }

    private void SetSpeed(float speed, float limit)
    {
        bool hasLimit = limit > 1;
        SpeedText.Text = hasLimit ? $"{speed:0} / {limit:0} км/ч" : $"{speed:0} км/ч";
        // Превышение больше чем на 5 км/ч — красным
        SetWarning(SpeedText, hasLimit && speed > limit + 5);
    }

    /// <summary>Красный текст для предупреждения; иначе — цвет из настроек (наследуется от панели).</summary>
    private static void SetWarning(System.Windows.Controls.TextBlock block, bool warn)
    {
        if (warn) block.Foreground = Brushes.OrangeRed;
        else block.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
    }

    private void SetBar(double progress) => BarFill.Width = Root.Width * Math.Clamp(progress, 0, 1);

    private static void SetLine(System.Windows.Controls.TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (Exception ex) when (ex is FormatException or NotSupportedException) { return Colors.White; }
    }

    // ───────────────────────────── Win32 ─────────────────────────────

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private void SetClickThrough(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        ex = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
    }

    /// <summary>Игра в полноэкранном режиме может перекрыть оверлей — раз в пару секунд поднимаем его наверх.</summary>
    private void KeepOnTop()
    {
        if (_hwnd == IntPtr.Zero || DateTime.Now - _lastTopmost < TimeSpan.FromSeconds(2)) return;
        _lastTopmost = DateTime.Now;
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
