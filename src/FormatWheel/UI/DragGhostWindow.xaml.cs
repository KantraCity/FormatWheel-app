using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FormatWheel;

// Превью у курсора во время нашего драга + кольцо прогресса.
// WS_EX_TRANSPARENT — окно пропускает мышь, не мешает броску.
public partial class DragGhostWindow : Window
{
    private const double AnchorX = 40, AnchorY = 40, DesignWidth = 220;

    private static readonly Brush AccentRing = Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0xA7, 0x8B, 0xFA), 0),
            new(Color.FromRgb(0x3B, 0x82, 0xF6), 0.5),
            new(Color.FromRgb(0x38, 0xBD, 0xF8), 1)
        }, new Point(0.1, 0), new Point(0.9, 1)));
    private static readonly Brush DoneRing = Frozen(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    private static readonly Brush ErrorRing = Frozen(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));
    private static readonly Brush PlaceholderThumb = Frozen(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));
    private static readonly Color AccentGlow = Color.FromRgb(0x6E, 0x9B, 0xFF);
    private static readonly Color DoneGlow = Color.FromRgb(0x22, 0xC5, 0x5E);
    private static readonly Color ErrorGlow = Color.FromRgb(0xEF, 0x44, 0x44);

    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _follow;
    private int _session; // чтобы прогресс старой конвертации не перерисовал новую
    private bool _done;
    private double _target;

    public bool Following { get; set; }
    public bool IsDone => _done;

    public DragGhostWindow()
    {
        InitializeComponent();
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.SetExStyle(_hwnd, add: Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW, remove: 0);

        _follow = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
        _follow.Tick += (_, _) => { if (Following) MoveToCursor(); };
    }

    public int Begin(BitmapSource? thumbnail, string badge, int fileCount)
    {
        int session = ResetState(badge);
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 0;
        Thumb.Background = thumbnail != null ? new ImageBrush(thumbnail) { Stretch = Stretch.UniformToFill } : PlaceholderThumb;
        CountText.Text = fileCount > 1 ? $"×{fileCount}" : "";
        CountBadge.Visibility = fileCount > 1 ? Visibility.Visible : Visibility.Collapsed;

        Following = true;
        Show();
        MoveToCursor();
        _follow.Start();

        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        var pop = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        return session;
    }

    public int Retarget(string badge) => ResetState(badge);

    private int ResetState(string badge)
    {
        int session = ++_session;
        _done = false;
        _target = 0;
        Ring.BeginAnimation(ProgressRing.ValueProperty, null);
        Ring.Value = 0;
        Ring.RingBrush = AccentRing;
        RingGlow.Color = AccentGlow;
        BadgeText.Text = badge;
        Status.Text = Loc.S.Converting;
        Percent.Text = "0%";
        Percent.Foreground = Brushes.White;
        return session;
    }

    public void SetDimmed(bool dimmed)
    {
        if (!IsVisible) return;
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(dimmed ? 0 : 1, TimeSpan.FromMilliseconds(120)));
    }

    public void SetProgress(int session, double value)
    {
        if (session != _session || _done) return;
        value = Math.Clamp(value, 0, 0.99);
        if (value <= _target) return;
        _target = value;
        Ring.BeginAnimation(ProgressRing.ValueProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        Percent.Text = $"{(int)Math.Round(value * 100)}%";
    }

    public void SetDone(int session, bool success)
    {
        if (session != _session || _done) return;
        _done = true;
        Ring.BeginAnimation(ProgressRing.ValueProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        Ring.RingBrush = success ? DoneRing : ErrorRing;
        RingGlow.Color = success ? DoneGlow : ErrorGlow;
        Status.Text = success ? Loc.S.Done : Loc.S.Failed;
        Percent.Text = success ? "✓" : "!";
        Percent.Foreground = success ? DoneRing : ErrorRing;
    }

    // после броска таем вместе с курсором, а не замираем на месте
    public void FadeOutFollowing()
    {
        if (!IsVisible) return;
        int session = _session;
        Following = true;
        var duration = TimeSpan.FromMilliseconds(160);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => { if (session == _session) HideNow(); };
        Root.BeginAnimation(OpacityProperty, fade);
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, duration) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, duration) { EasingFunction = ease });
    }

    public void HideNow()
    {
        _session++;
        Following = false;
        _follow.Stop();
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 0;
        Hide();
    }

    public void MoveToCursor()
    {
        Native.GetCursorPos(out var p);
        Native.GetWindowRect(_hwnd, out var r);
        double k = (r.Right - r.Left) / DesignWidth;
        if (k <= 0) k = 1;
        Native.SetWindowPos(_hwnd, IntPtr.Zero,
            (int)Math.Round(p.X - AnchorX * k), (int)Math.Round(p.Y - AnchorY * k), 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }
}
