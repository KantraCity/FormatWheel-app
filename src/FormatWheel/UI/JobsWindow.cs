using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;

namespace FormatWheel;

// Прогресс конвертации, которую доделываем в фоне (файл бросили раньше, чем он был готов).
// Карточка с курсором при броске исчезает, и без этой панели казалось, что программа зависла:
// большой документ в PDF или видео делаются минуту и дольше.
internal sealed class JobProgress
{
    public double Value { get; private set; }
    public event Action<double>? Changed;

    public void Report(double value)
    {
        if (value <= Value) return;
        Value = value;
        Changed?.Invoke(value);
    }
}

// Панель у трея: по карточке на каждую фоновую конвертацию. Пристраивается к своему значку в трее
// (Windows сама говорит, где он; если значок спрятан — где кнопка «^»), с той стороны, где панель задач.
internal sealed class JobsWindow : Window
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    private const double CardWidth = 250, Gap = 10;
    private const double ChevronWidth = 32; // кнопка «^» в Windows 11 — у начала области уведомлений

    // где на экране значок программы в трее; задаёт App, потому что значок — его
    public static Func<Native.RECT?>? TrayIconRect { get; set; }

    private readonly IntPtr _hwnd;
    private static readonly TimeSpan DoneLinger = TimeSpan.FromSeconds(6), FailedLinger = TimeSpan.FromSeconds(15);

    private static readonly Brush Accent = Frozen(new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromRgb(0xA7, 0x8B, 0xFA), 0),
        new(Color.FromRgb(0x3B, 0x82, 0xF6), 0.5),
        new(Color.FromRgb(0x38, 0xBD, 0xF8), 1)
    }, new Point(0, 0), new Point(1, 0)));
    private static readonly Brush DoneBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)));
    private static readonly Brush ErrorBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));
    private static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)));

    private readonly StackPanel _cards = new();

    public JobsWindow()
    {
        Title = "Format Wheel jobs";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        UseLayoutRounding = true;
        SizeToContent = SizeToContent.Height;
        Width = CardWidth;
        FontFamily = new FontFamily("Segoe UI");
        Content = _cards;

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.SetExStyle(_hwnd, add: Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW, remove: 0);
        SizeChanged += (_, _) => Place();
    }

    // files — что конвертируем, folder — куда положим готовое, cancel — остановить конвертацию (крестик)
    public Card Add(string[] files, string folder, JobProgress progress, Action? cancel) =>
        Show(new Card(this, files, null, folder, progress, cancel));

    // действие из расширения (печать и т.п.): ничего не сохраняем, action — что делаем
    public Card AddAction(string[] files, string action, JobProgress progress, Action? cancel) =>
        Show(new Card(this, files, action, null, progress, cancel));

    private Card Show(Card card)
    {
        _cards.Children.Add(card.Root);
        if (!IsVisible) Show();
        Place();
        return card;
    }

    private void Remove(Card card)
    {
        _cards.Children.Remove(card.Root);
        if (_cards.Children.Count == 0) Hide();
    }

    // всё в пикселях экрана: панель задач и значки трея живут в них
    private void Place()
    {
        double k = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Round(ActualWidth * k), h = (int)Math.Round(ActualHeight * k), gap = (int)Math.Round(Gap * k);
        var (x, y) = NextToTray(w, h, gap) ?? Corner(w, h, gap);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    // по центру значка, вплотную к панели задач с её внутренней стороны; null — панель задач не нашлась
    private static (int X, int Y)? NextToTray(int w, int h, int gap)
    {
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !Native.GetWindowRect(taskbar, out var bar)) return null;
        var barCenter = new Native.POINT { X = (bar.Left + bar.Right) / 2, Y = (bar.Top + bar.Bottom) / 2 };
        var work = Native.WorkAreaAt(barCenter);
        bool horizontal = bar.Right - bar.Left > bar.Bottom - bar.Top;
        if ((IconCenter(bar) ?? ChevronCenter(taskbar, horizontal)) is not { } a) return null;

        int x, y;
        if (horizontal)
        {
            x = a.X - w / 2;
            y = barCenter.Y > (work.Top + work.Bottom) / 2 ? bar.Top - h - gap : bar.Bottom + gap; // снизу или сверху
        }
        else
        {
            y = a.Y - h / 2;
            x = barCenter.X > (work.Left + work.Right) / 2 ? bar.Left - w - gap : bar.Right + gap; // справа или слева
        }
        // не вылезаем за экран (и за спрятанную панель задач)
        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - w - gap));
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - h - gap));
        return (x, y);
    }

    // свой значок; если он спрятан в «^», Windows вернёт кнопку «^» — тоже то, что нужно
    private static Native.POINT? IconCenter(Native.RECT bar)
    {
        if (TrayIconRect?.Invoke() is not { } r || r.Right <= r.Left || r.Bottom <= r.Top) return null;
        var c = new Native.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
        // открытое окно скрытых значков — не панель задач, к нему не липнем
        return c.X >= bar.Left && c.X <= bar.Right && c.Y >= bar.Top && c.Y <= bar.Bottom ? c : null;
    }

    // запасной вариант: кнопка «^» в начале области уведомлений
    private static Native.POINT? ChevronCenter(IntPtr taskbar, bool horizontal)
    {
        IntPtr notify = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero || !Native.GetWindowRect(notify, out var tray)) return null;
        int half = (int)Math.Round(ChevronWidth / 2 * GetDpiForWindow(taskbar) / 96.0);
        return horizontal
            ? new Native.POINT { X = tray.Left + half, Y = (tray.Top + tray.Bottom) / 2 }
            : new Native.POINT { X = (tray.Left + tray.Right) / 2, Y = tray.Top + half };
    }

    private static (int X, int Y) Corner(int w, int h, int gap)
    {
        var area = Native.WorkAreaAt(new Native.POINT { X = 0, Y = 0 }); // основной монитор
        return (area.Right - w - gap, area.Bottom - h - gap);
    }

    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }

    internal sealed class Card
    {
        private readonly JobsWindow _owner;
        private readonly string? _folder; // null — действие, файл никуда не сохраняется
        private readonly JobProgress _progress;
        private readonly ScaleTransform _fill = new(0, 1);
        private readonly Rectangle _bar;
        private readonly Rectangle _shimmer;
        private readonly TextBlock _percent, _status, _cancel;
        private string[]? _saved;
        private bool _finished, _cancelled;

        public Border Root { get; }

        public Card(JobsWindow owner, string[] files, string? action, string? folder, JobProgress progress, Action? cancel)
        {
            _owner = owner;
            _folder = folder;
            _progress = progress;

            // минимум: «имя · проценты» и тонкая полоса; куда сохранится — в подсказке,
            // крестик — только под мышью, вторая строка — только для текста ошибки
            var title = new TextBlock
            {
                Text = (action != null ? action + " · " : "") + (files.Length == 1 ? Path.GetFileName(files[0]) : Loc.S.FileCount(files.Length)),
                Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
            };
            _percent = new TextBlock { Foreground = Dim, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(_percent, Dock.Right);
            // крестик: остановить конвертацию, файл не сохраняем
            _cancel = new TextBlock
            {
                Text = "✕", FontSize = 11, Foreground = Dim, Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.S.JobCancel, Visibility = Visibility.Collapsed
            };
            _cancel.MouseEnter += (_, _) => _cancel.Foreground = Brushes.White;
            _cancel.MouseLeave += (_, _) => _cancel.Foreground = Dim;
            _cancel.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true; // не считать кликом по карточке
                if (_finished || cancel == null) return;
                _cancelled = true;
                _percent.Text = "…";
                _cancel.Visibility = Visibility.Collapsed;
                cancel();
            };
            DockPanel.SetDock(_cancel, Dock.Right);
            var header = new DockPanel { LastChildFill = true };
            header.Children.Add(_cancel);
            header.Children.Add(_percent);
            header.Children.Add(title);

            _status = new TextBlock
            {
                Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxHeight = 30,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0), Visibility = Visibility.Collapsed
            };

            _bar = new Rectangle { Fill = Accent, RenderTransform = _fill, HorizontalAlignment = HorizontalAlignment.Stretch };
            // бегущий блик: даже когда процент долго стоит (Edge печатает PDF), видно, что работа идёт
            _shimmer = new Rectangle
            {
                Width = 60, HorizontalAlignment = HorizontalAlignment.Left,
                Fill = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0),
                    new(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 0.5),
                    new(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 1)
                }, new Point(0, 0), new Point(1, 0))),
                RenderTransform = new TranslateTransform(-60, 0)
            };
            var track = new Grid
            {
                Height = 2, ClipToBounds = true, Margin = new Thickness(0, 6, 0, 0),
                Background = Frozen(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)))
            };
            track.Children.Add(_bar);
            track.Children.Add(_shimmer);
            if (!Perf.Lite)
                _shimmer.RenderTransform.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(-60, CardWidth, TimeSpan.FromMilliseconds(1600)) { RepeatBehavior = RepeatBehavior.Forever });

            var body = new StackPanel();
            body.Children.Add(header);
            body.Children.Add(_status);
            body.Children.Add(track);

            Root = new Border
            {
                Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(10, 7, 10, 8), CornerRadius = new CornerRadius(10),
                Child = body, Cursor = Cursors.Arrow, Opacity = 0, ToolTip = folder != null ? Loc.S.JobSavingTo(FolderName(folder)) : action,
                Background = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0x10, 0x12, 0x18)))
            };
            Root.MouseEnter += (_, _) => { if (!_finished && !_cancelled && cancel != null) _cancel.Visibility = Visibility.Visible; };
            Root.MouseLeave += (_, _) => _cancel.Visibility = Visibility.Collapsed;
            Root.MouseLeftButtonUp += (_, _) => OnClick();
            Root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));

            SetValue(progress.Value);
            progress.Changed += OnChanged;
        }

        private void OnChanged(double value) => Root.Dispatcher.InvokeAsync(() => { if (!_finished) SetValue(value); });

        private void SetValue(double value)
        {
            value = Math.Clamp(value, 0, 0.99);
            if (Perf.Lite) _fill.ScaleX = value;
            else _fill.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(250)));
            _percent.Text = $"{(int)Math.Round(value * 100)}%";
        }

        // действие выполнено
        public void Done()
        {
            Finish(DoneBrush, "✓");
            Root.ToolTip = null;
            Linger(DoneLinger);
        }

        public void Done(string[] saved)
        {
            Finish(DoneBrush, "✓");
            _saved = saved;
            Root.Cursor = Cursors.Hand;
            Root.ToolTip = Loc.S.JobSavedIn(FolderName(_folder!));
            Linger(DoneLinger);
        }

        public void Failed(string message)
        {
            Finish(ErrorBrush, "!");
            _status.Text = message;
            _status.Visibility = Visibility.Visible;
            Root.ToolTip = message;
            Linger(FailedLinger);
        }

        public void Remove()
        {
            Finish(DoneBrush, "");
            _owner.Remove(this);
        }

        private void Finish(Brush brush, string mark)
        {
            _finished = true;
            _cancel.Visibility = Visibility.Collapsed;
            _progress.Changed -= OnChanged;
            _shimmer.RenderTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _shimmer.Visibility = Visibility.Collapsed;
            _bar.Fill = brush;
            _fill.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
            _percent.Text = mark;
            _percent.Foreground = brush;
        }

        // пока мышь над карточкой — не прячем
        private void Linger(TimeSpan time)
        {
            var timer = new DispatcherTimer { Interval = time };
            timer.Tick += (_, _) =>
            {
                if (Root.IsMouseOver) return;
                timer.Stop();
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
                fade.Completed += (_, _) => _owner.Remove(this);
                Root.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            timer.Start();
        }

        // готовый файл — показываем в Проводнике
        private void OnClick()
        {
            if (_saved == null) return;
            try
            {
                if (_saved.Length == 1) Process.Start("explorer.exe", $"/select,\"{_saved[0]}\"");
                else if (_folder != null) Process.Start("explorer.exe", $"\"{_folder}\"");
            }
            catch (Exception ex) { Log.Write("Open saved file: " + ex.Message); }
        }

        private static string FolderName(string folder) =>
            string.Equals(folder.TrimEnd('\\'), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), StringComparison.OrdinalIgnoreCase)
                ? Loc.S.Desktop
                : Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } name ? name : folder;
    }
}
