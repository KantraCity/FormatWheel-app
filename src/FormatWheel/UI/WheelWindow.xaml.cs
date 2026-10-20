using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FormatWheel;

// Один лепесток для отрисовки. Label/Hint/Icon — что показать, остальное колесу не важно.
internal sealed record WheelItem(string Label, string Hint, BitmapSource? Icon, bool IsCurrent);

public partial class WheelWindow : Window
{
    private const double WheelSize = 380, C = WheelSize / 2;
    private const double CenterR = 56, InnerR = 68, OuterR = 160, HitR = 170;
    private const double GapDeg = 1.2, LiftPx = 7;
    private const double FoldedScale = 0.15, FoldedAngle = -120;

    private static readonly Color AccentColor = Color.FromRgb(0x6E, 0x9B, 0xFF);
    private static readonly Brush AccentFill = Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0xA7, 0x8B, 0xFA), 0),
            new(Color.FromRgb(0x3B, 0x82, 0xF6), 0.5),
            new(Color.FromRgb(0x38, 0xBD, 0xF8), 1)
        }, new Point(0.1, 0), new Point(0.9, 1)));
    // мягкая подсветка невыбранного лепестка — почти незаметный верхний блик, как на стекле Win11
    private static readonly Brush PetalFill = Frozen(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromArgb(0xEA, 0x2A, 0x2E, 0x38), 0),
            new(Color.FromArgb(0xE6, 0x1A, 0x1D, 0x24), 1)
        }, new Point(0.5, 0), new Point(0.5, 1)));
    private static readonly Brush PetalStroke = Frozen(new LinearGradientBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF), 90));
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush HintBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush CurrentBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xA5, 0xC8, 0xFF)));
    private static readonly Brush KeyOnFill = Frozen(new SolidColorBrush(Color.FromArgb(0xCC, 0x3B, 0x6E, 0xE6)));
    private static readonly Brush KeyOnStroke = Frozen(new SolidColorBrush(Color.FromRgb(0x6E, 0x9B, 0xFF)));
    private static readonly Brush KeyOffFill = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x1E, 0x21, 0x2A)));
    private static readonly Brush KeyOffStroke = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush KeyOffText = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush HitBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)));
    private static readonly Brush EmptyThumb = Frozen(new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)));

    // Halo — свечение выбранного лепестка: широкая полупрозрачная обводка вместо DropShadowEffect (без размытия, дёшево)
    private sealed record Petal(Canvas Group, ScaleTransform Grow, TranslateTransform Lift, Path Highlight, Path Halo, TextBlock Label, double AngleDeg);

    private static readonly Brush HaloBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0x6E, 0x9B, 0xFF)));

    private readonly List<Petal> _petals = new();
    private readonly DispatcherTimer _timer;
    private readonly IntPtr _hwnd;
    private IReadOnlyList<WheelItem> _items = Array.Empty<WheelItem>();
    private Canvas _arrow = null!;
    private Native.POINT _lastCursor = new() { X = int.MinValue };
    private double _lastArrowAngle = double.NaN;
    private RotateTransform _arrowRotate = null!;

    private bool _acceptDrops = true;
    private bool _revealed;
    private bool _confine;
    private int _closeSession;
    private string _title = "";
    private string? _message;

    public int SelectedIndex { get; private set; } = -1;

    public event Action<string[]>? FilesEntered;
    public event Action? DragLeftWindow;
    public event Action? DroppedOnWheel;

    public WheelWindow()
    {
        InitializeComponent();
        BuildArrow();
        BuildKeyChips();

        // 60 раз в секунду (30 в лёгком режиме); работа — только когда курсор сдвинулся
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(Perf.Lite ? 33 : 16) };
        _timer.Tick += (_, _) => Tick();

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetNoActivate(true);

        DragEnter += OnDragEnterOrOver;
        DragOver += OnDragEnterOrOver;
        DragLeave += (_, _) => DragLeftWindow?.Invoke();
        Drop += (_, e) => { e.Effects = DragDropEffects.None; e.Handled = true; DroppedOnWheel?.Invoke(); };

        ResetVisuals();
    }

    // подписи клавиш сверху: показываем только те модификаторы, за которыми есть меню, зажатые — подсвечены.
    // Если других меню нет вовсе, подписи прячем: подсказывать нечего.
    public void SetKeys(bool hasCtrl, bool hasAlt, bool ctrlDown, bool altDown)
    {
        SetChip(_chipShift, visible: true, down: true);
        SetChip(_chipCtrl, hasCtrl, ctrlDown);
        SetChip(_chipAlt, hasAlt, altDown);
        KeyChips.Opacity = hasCtrl || hasAlt ? 1 : 0;
    }

    private static void SetChip(Border chip, bool visible, bool down)
    {
        chip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        chip.Background = down ? KeyOnFill : KeyOffFill;
        chip.BorderBrush = down ? KeyOnStroke : KeyOffStroke;
        ((TextBlock)chip.Child).Foreground = down ? LabelBrush : KeyOffText;
    }

    internal void SetItems(IReadOnlyList<WheelItem> items)
    {
        _items = items;
        BuildPetals();
    }

    internal void ShowAt(Native.POINT cursor, bool acceptDrops)
    {
        _acceptDrops = acceptDrops;
        _closeSession++;
        Native.SetExStyle(_hwnd, add: 0, remove: Native.WS_EX_TRANSPARENT);
        ResetVisuals();
        Show();
        PlaceNear(cursor);
        Dispatcher.InvokeAsync(() => PlaceNear(cursor), DispatcherPriority.Loaded);
        _timer.Start();
    }

    public void Reveal(string title, string? message = null)
    {
        _title = title;
        _message = message;
        SetSelected(-1, force: true);
        if (_revealed) return;
        _revealed = true;
        _confine = true;

        GetGeometry(out double cx, out double cy, out double k);
        Native.GetCursorPos(out var p);
        if (Math.Sqrt(Math.Pow(p.X - cx, 2) + Math.Pow(p.Y - cy, 2)) > CenterR * k)
            Native.SetCursorPos((int)Math.Round(cx), (int)Math.Round(cy));
        ConfineTo(cx, cy, k);
        _lastCursor.X = int.MinValue; // пересчитать выбор на ближайшем тике

        if (Perf.Lite)
        {
            // без пружины и поворота: только быстрое проявление
            Stop(ContentScale, ScaleTransform.ScaleXProperty, 1);
            Stop(ContentScale, ScaleTransform.ScaleYProperty, 1);
            Stop(ContentRotate, RotateTransform.AngleProperty, 0);
            WheelContent.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ms(90)));
            AnimatePetalsIn();
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var pop = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
        WheelContent.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ms(140)) { EasingFunction = ease });
        ContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, Ms(300)) { EasingFunction = pop });
        ContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, Ms(300)) { EasingFunction = pop });
        ContentRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, Ms(320)) { EasingFunction = ease });
        AnimatePetalsIn();
    }

    public void SetThumbnail(BitmapSource? thumbnail)
    {
        if (thumbnail == null) return;
        CenterThumb.Fill = new ImageBrush(thumbnail) { Stretch = Stretch.UniformToFill };
        CenterThumb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(180)));
    }

    public void CloseAnimated(bool inputTransparentNow)
    {
        _timer.Stop();
        _confine = false;
        Native.ReleaseCursor();
        if (inputTransparentNow) MakeInputTransparent();

        if (!IsVisible) return;
        if (!_revealed) { HideWheel(); return; }

        int session = ++_closeSession;
        var duration = Ms(190);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(0, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => { if (session == _closeSession) HideWheel(); };

        if (Perf.Lite)
        {
            // только гаснет: складывание с поворотом на слабом ПК стоит десятка тяжёлых кадров
            WheelContent.BeginAnimation(OpacityProperty, fade);
            return;
        }

        ContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(FoldedScale, duration) { EasingFunction = ease });
        ContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(FoldedScale, duration) { EasingFunction = ease });
        ContentRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(FoldedAngle, duration) { EasingFunction = ease });
        WheelContent.BeginAnimation(OpacityProperty, fade);
    }

    public void MakeInputTransparent()
    {
        HitArea.Fill = Brushes.Transparent;
        AllowDrop = false;
        Native.SetExStyle(_hwnd, add: Native.WS_EX_TRANSPARENT, remove: 0);
    }

    public void HideWheel()
    {
        _closeSession++;
        _timer.Stop();
        _confine = false;
        Native.ReleaseCursor();
        Hide();
        ResetVisuals();
        Native.SetExStyle(_hwnd, add: 0, remove: Native.WS_EX_TRANSPARENT);
        SetNoActivate(true);
    }

    private void OnDragEnterOrOver(object sender, DragEventArgs e)
    {
        e.Effects = (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : e.AllowedEffects;
        e.Handled = true;

        // GetData — межпроцессный вызов, не дёргаем на каждом движении мыши
        if (e.RoutedEvent != DragEnterEvent && _revealed) return;

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            FilesEntered?.Invoke(files);
        }
    }

    private void Tick()
    {
        if (!_revealed) return;

        // курсор не двигался — пересчитывать нечего (на слабом ПК каждый лишний кадр прозрачного окна дорог)
        Native.GetCursorPos(out var p);
        if (p.X == _lastCursor.X && p.Y == _lastCursor.Y) return;
        _lastCursor = p;

        GetGeometry(out double cx, out double cy, out double k);
        double dx = (p.X - cx) / k, dy = (p.Y - cy) / k;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        if (_confine)
        {
            double maxR = OuterR - 4;
            if (dist > maxR)
            {
                // вырвался за колесо (ClipCursor сбрасывает сама Windows, например при смене окна) — вернём
                ConfineTo(cx, cy, k);
                double s = maxR / dist;
                dx *= s; dy *= s; dist = maxR;
                Native.SetCursorPos((int)Math.Round(cx + dx * k), (int)Math.Round(cy + dy * k));
            }
        }

        int index = -1;
        if (dist > CenterR && _petals.Count > 0)
        {
            double step = 360.0 / _petals.Count;
            double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
            double relative = ((angle + 90 + step / 2) % 360 + 360) % 360; // первый лепесток сверху
            index = (int)(relative / step) % _petals.Count;
        }

        // стрелку поворачиваем, только если угол заметно изменился — меньше перерисовок
        if (dist > 4)
        {
            double angle = Math.Atan2(dy, dx) * 180 / Math.PI + 90;
            if (double.IsNaN(_lastArrowAngle) || Math.Abs(angle - _lastArrowAngle) > 0.7)
            {
                _lastArrowAngle = angle;
                _arrowRotate.Angle = angle;
            }
        }
        double arrowOpacity = Math.Round(Math.Clamp((dist - 8) / (CenterR - 8), 0, 1), 2);
        if (_arrow.Opacity != arrowOpacity) _arrow.Opacity = arrowOpacity;

        SetSelected(index);
    }

    private void ConfineTo(double cx, double cy, double k) =>
        Native.ConfineCursor(new Native.RECT
        {
            Left = (int)(cx - HitR * k), Top = (int)(cy - HitR * k),
            Right = (int)(cx + HitR * k), Bottom = (int)(cy + HitR * k)
        });

    private void SetSelected(int index, bool force = false)
    {
        if (index == SelectedIndex && !force) return;
        int previous = SelectedIndex;
        SelectedIndex = index;

        // анимируем только два лепестка — тот, с которого ушли, и тот, на который пришли
        for (int i = 0; i < _petals.Count; i++)
        {
            if (!force && i != previous && i != index) continue;
            bool selected = i == index;
            var petal = _petals[i];
            double rad = petal.AngleDeg * Math.PI / 180;
            Animate(petal.Highlight, OpacityProperty, selected ? 1 : 0);
            Animate(petal.Halo, OpacityProperty, selected ? 1 : 0);
            Animate(petal.Lift, TranslateTransform.XProperty, selected ? Math.Cos(rad) * LiftPx : 0);
            Animate(petal.Lift, TranslateTransform.YProperty, selected ? Math.Sin(rad) * LiftPx : 0);
            petal.Label.FontWeight = selected ? FontWeights.Bold : FontWeights.SemiBold;
        }

        Animate(CenterShade, OpacityProperty, index >= 0 ? 1 : 0.35);
        PillText.Text = _message != null ? $"{_title}   ·   {_message}"
            : index >= 0 && index < _items.Count ? $"{_title}   →   {_items[index].Label}"
            : $"{_title}   ·   {Loc.S.PickOne}";
    }

    private void ResetVisuals()
    {
        _revealed = false;
        _confine = false;

        Stop(WheelContent, OpacityProperty, 0);
        Stop(ContentScale, ScaleTransform.ScaleXProperty, FoldedScale);
        Stop(ContentScale, ScaleTransform.ScaleYProperty, FoldedScale);
        Stop(ContentRotate, RotateTransform.AngleProperty, FoldedAngle);
        Stop(CenterThumb, OpacityProperty, 1);
        Stop(CenterShade, OpacityProperty, 0);
        CenterThumb.Fill = EmptyThumb;

        foreach (var petal in _petals)
        {
            Stop(petal.Group, OpacityProperty, 0);
            Stop(petal.Grow, ScaleTransform.ScaleXProperty, 0.5);
            Stop(petal.Grow, ScaleTransform.ScaleYProperty, 0.5);
            Stop(petal.Highlight, OpacityProperty, 0);
            Stop(petal.Halo, OpacityProperty, 0);
            Stop(petal.Lift, TranslateTransform.XProperty, 0);
            Stop(petal.Lift, TranslateTransform.YProperty, 0);
            petal.Label.FontWeight = FontWeights.SemiBold;
        }
        _arrow.Opacity = 0;
        _lastCursor.X = int.MinValue;
        _lastArrowAngle = double.NaN;

        HitArea.Fill = HitBrush;
        AllowDrop = _acceptDrops;
        _title = "";
        _message = null;
        SelectedIndex = -2;
        SetSelected(-1, force: true);
    }

    private void AnimatePetalsIn()
    {
        if (Perf.Lite)
        {
            // все лепестки сразу, без поочерёдного «распускания»
            foreach (var petal in _petals)
            {
                Stop(petal.Grow, ScaleTransform.ScaleXProperty, 1);
                Stop(petal.Grow, ScaleTransform.ScaleYProperty, 1);
                Stop(petal.Group, OpacityProperty, 1);
            }
            return;
        }

        var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        for (int i = 0; i < _petals.Count; i++)
        {
            var begin = TimeSpan.FromMilliseconds(30 + i * 20);
            var petal = _petals[i];
            petal.Group.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(160)) { BeginTime = begin });
            var grow = new DoubleAnimation(0.5, 1, Ms(220)) { BeginTime = begin, EasingFunction = ease };
            petal.Grow.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            petal.Grow.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    private void GetGeometry(out double cx, out double cy, out double k)
    {
        Native.GetWindowRect(_hwnd, out var r);
        double w = Math.Max(1, r.Right - r.Left);
        k = w / WheelSize;
        cx = r.Left + w / 2;
        cy = r.Top + (r.Bottom - r.Top) / 2.0;
    }

    private void PlaceNear(Native.POINT cursor)
    {
        Native.GetWindowRect(_hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        double k = Math.Max(1, w) / WheelSize;
        var work = Native.WorkAreaAt(cursor);

        double R = OuterR * k;
        double cx = Math.Clamp(cursor.X, work.Left + R, Math.Max(work.Left + R, work.Right - R));
        double cy = Math.Clamp(cursor.Y, work.Top + R, Math.Max(work.Top + R, work.Bottom - R));

        double maxShift = (HitR - 14) * k;
        double sx = cx - cursor.X, sy = cy - cursor.Y, shift = Math.Sqrt(sx * sx + sy * sy);
        if (shift > maxShift)
        {
            cx = cursor.X + sx / shift * maxShift;
            cy = cursor.Y + sy / shift * maxShift;
        }

        Native.SetWindowPos(_hwnd, IntPtr.Zero, (int)Math.Round(cx - w / 2.0), (int)Math.Round(cy - h / 2.0), 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private void SetNoActivate(bool on) =>
        Native.SetExStyle(_hwnd,
            add: Native.WS_EX_TOOLWINDOW | (on ? Native.WS_EX_NOACTIVATE : 0),
            remove: on ? 0 : Native.WS_EX_NOACTIVATE);

    private void BuildPetals()
    {
        PetalCanvas.Children.Clear();
        _petals.Clear();

        var center = new Point(C, C);
        int n = _items.Count;
        double step = 360.0 / Math.Max(1, n);

        for (int i = 0; i < n; i++)
        {
            var item = _items[i];
            double mid = -90 + i * step;
            var geometry = RingSector(center, InnerR, OuterR, mid - step / 2 + GapDeg, mid + step / 2 - GapDeg);

            var lift = new TranslateTransform();
            var grow = new ScaleTransform(0.5, 0.5, C, C);
            var transforms = new TransformGroup();
            transforms.Children.Add(grow);
            transforms.Children.Add(lift);
            var group = new Canvas { Width = WheelSize, Height = WheelSize, IsHitTestVisible = false, RenderTransform = transforms, Opacity = 0 };

            group.Children.Add(new Path { Data = geometry, Fill = PetalFill, Stroke = PetalStroke, StrokeThickness = 1 });

            var halo = new Path { Data = geometry, Stroke = HaloBrush, StrokeThickness = 8, StrokeLineJoin = PenLineJoin.Round, Opacity = 0 };
            var highlight = new Path { Data = geometry, Fill = AccentFill, Opacity = 0 };
            group.Children.Add(halo);
            group.Children.Add(highlight);

            var stack = new StackPanel { Width = 84 };
            if (item.Icon != null)
                stack.Children.Add(new Image { Source = item.Icon, Width = 22, Height = 22, Margin = new Thickness(0, 0, 0, 2), HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock
            {
                Text = item.Label,
                FontSize = item.Label.Length > 6 ? 13 : n > 8 ? 14 : 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = LabelBrush,
                TextAlignment = TextAlignment.Center
            });
            if (!string.IsNullOrEmpty(item.Hint) || item.IsCurrent)
                stack.Children.Add(new TextBlock
                {
                    Text = item.IsCurrent ? "✓ " + item.Hint : item.Hint,
                    FontSize = 9.5,
                    Foreground = item.IsCurrent ? CurrentBrush : HintBrush,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 1, 0, 0)
                });

            double rad = mid * Math.PI / 180, labelR = (InnerR + OuterR) / 2;
            Canvas.SetLeft(stack, C + labelR * Math.Cos(rad) - 42);
            Canvas.SetTop(stack, C + labelR * Math.Sin(rad) - (item.Icon != null ? 28 : 17));
            group.Children.Add(stack);

            PetalCanvas.Children.Add(group);
            _petals.Add(new Petal(group, grow, lift, highlight, halo,
                (TextBlock)stack.Children[item.Icon != null ? 1 : 0], mid));
        }

        PetalCanvas.Children.Add(_arrow);
        if (_revealed) AnimatePetalsIn(); // переключили слот при открытом колесе
    }

    private Border _chipShift = null!, _chipCtrl = null!, _chipAlt = null!;

    private void BuildKeyChips()
    {
        Border Chip(string text)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 1, 6, 2),
                Margin = new Thickness(2, 0, 2, 0),
                Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold }
            };
            KeyChips.Children.Add(chip);
            return chip;
        }
        _chipShift = Chip("Shift");
        _chipCtrl = Chip("Ctrl");
        _chipAlt = Chip("Alt");
        SetKeys(false, false, false, false);
    }

    private void BuildArrow()
    {
        const double r = 62;
        Point At(double deg)
        {
            double rad = deg * Math.PI / 180;
            return new Point(C + r * Math.Cos(rad), C + r * Math.Sin(rad));
        }

        var figure = new PathFigure { StartPoint = At(-90 - 22), IsClosed = false };
        figure.Segments.Add(new ArcSegment(At(-90 + 22), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        // свечение — та же дуга пошире и полупрозрачная под основной, вместо DropShadowEffect
        _arrowRotate = new RotateTransform(0, C, C);
        _arrow = new Canvas { Width = WheelSize, Height = WheelSize, RenderTransform = _arrowRotate, Opacity = 0, IsHitTestVisible = false };
        _arrow.Children.Add(new Path
        {
            Data = geometry, Stroke = HaloBrush, StrokeThickness = 10,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        });
        _arrow.Children.Add(new Path
        {
            Data = geometry, Stroke = AccentFill, StrokeThickness = 3.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        });
    }

    private static Geometry RingSector(Point c, double r1, double r2, double a0, double a1)
    {
        Point At(double r, double deg)
        {
            double rad = deg * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }

        bool large = a1 - a0 > 180;
        var figure = new PathFigure { StartPoint = At(r1, a0), IsClosed = true };
        figure.Segments.Add(new LineSegment(At(r2, a0), true));
        figure.Segments.Add(new ArcSegment(At(r2, a1), new Size(r2, r2), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(At(r1, a1), true));
        figure.Segments.Add(new ArcSegment(At(r1, a0), new Size(r1, r1), 0, large, SweepDirection.Counterclockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static readonly IEasingFunction SelectEase = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    // в лёгком режиме выбор лепестка — мгновенно, без анимации
    private static void Animate(IAnimatable target, DependencyProperty property, double to)
    {
        if (Perf.Lite) { Stop(target, property, to); return; }
        target.BeginAnimation(property, new DoubleAnimation(to, Ms(130)) { EasingFunction = SelectEase });
    }

    private static IEasingFunction Frozen(CubicEase ease) { ease.Freeze(); return ease; }

    private static void Stop(IAnimatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, value);
    }

    private static Duration Ms(int ms) => Perf.Ms(ms);

    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }
}
