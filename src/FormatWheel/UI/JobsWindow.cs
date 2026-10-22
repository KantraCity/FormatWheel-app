using System;
using System.Diagnostics;
using System.IO;
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

// Панель у трея (правый нижний угол рабочей области): по карточке на каждую фоновую конвертацию.
internal sealed class JobsWindow : Window
{
    private const double CardWidth = 300, Gap = 12;
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
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        Content = _cards;

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.SetExStyle(hwnd, add: Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW, remove: 0);
        SizeChanged += (_, _) => Place();
    }

    // files — что конвертируем, folder — куда положим готовое, cancel — остановить конвертацию (крестик)
    public Card Add(string[] files, string folder, JobProgress progress, Action? cancel)
    {
        var card = new Card(this, files, folder, progress, cancel);
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

    // рабочая область основного монитора уже без панели задач, в единицах WPF
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - CardWidth - Gap;
        Top = area.Bottom - ActualHeight - Gap;
    }

    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }

    internal sealed class Card
    {
        private readonly JobsWindow _owner;
        private readonly string _folder;
        private readonly JobProgress _progress;
        private readonly ScaleTransform _fill = new(0, 1);
        private readonly Rectangle _bar;
        private readonly Rectangle _shimmer;
        private readonly TextBlock _percent, _status, _cancel;
        private string[]? _saved;
        private bool _finished;

        public Border Root { get; }

        public Card(JobsWindow owner, string[] files, string folder, JobProgress progress, Action? cancel)
        {
            _owner = owner;
            _folder = folder;
            _progress = progress;

            var title = new TextBlock
            {
                Text = files.Length == 1 ? Path.GetFileName(files[0]) : Loc.S.FileCount(files.Length),
                Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _percent = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(_percent, Dock.Right);
            // крестик: остановить конвертацию, файл не сохраняем
            _cancel = new TextBlock
            {
                Text = "✕", FontSize = 12, Foreground = Dim, Margin = new Thickness(10, 0, 0, 0), Cursor = Cursors.Hand,
                ToolTip = Loc.S.JobCancel, Visibility = cancel != null ? Visibility.Visible : Visibility.Collapsed
            };
            _cancel.MouseEnter += (_, _) => _cancel.Foreground = Brushes.White;
            _cancel.MouseLeave += (_, _) => _cancel.Foreground = Dim;
            _cancel.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true; // не считать кликом по карточке
                if (_finished || cancel == null) return;
                _status.Text = Loc.S.JobCancelling;
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
                Text = Loc.S.JobSavingTo(FolderName(folder)), Foreground = Dim, FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 8)
            };

            _bar = new Rectangle { Fill = Accent, RadiusX = 2, RadiusY = 2, RenderTransform = _fill, HorizontalAlignment = HorizontalAlignment.Stretch };
            // бегущий блик: даже когда процент долго стоит (Edge печатает PDF), видно, что работа идёт
            _shimmer = new Rectangle
            {
                Width = 70, HorizontalAlignment = HorizontalAlignment.Left,
                Fill = Frozen(new LinearGradientBrush(new GradientStopCollection
                {
                    new(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0),
                    new(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 0.5),
                    new(Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 1)
                }, new Point(0, 0), new Point(1, 0))),
                RenderTransform = new TranslateTransform(-70, 0)
            };
            var track = new Grid
            {
                Height = 4, ClipToBounds = true,
                Background = Frozen(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)))
            };
            track.Children.Add(_bar);
            track.Children.Add(_shimmer);
            track.Clip = new RectangleGeometry(new Rect(0, 0, CardWidth - 26, 4), 2, 2);
            if (!Perf.Lite)
                _shimmer.RenderTransform.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(-70, CardWidth, TimeSpan.FromMilliseconds(1400)) { RepeatBehavior = RepeatBehavior.Forever });

            var body = new StackPanel();
            body.Children.Add(header);
            body.Children.Add(_status);
            body.Children.Add(track);

            Root = new Border
            {
                Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(12, 10, 12, 12), CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1), Child = body, Cursor = Cursors.Arrow,
                Background = Frozen(new LinearGradientBrush(Color.FromArgb(0xF0, 0x1E, 0x21, 0x2A), Color.FromArgb(0xF0, 0x14, 0x16, 0x20), 90)),
                BorderBrush = Frozen(new LinearGradientBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 90)),
                Opacity = 0
            };
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

        public void Done(string[] saved)
        {
            Finish(DoneBrush, "✓");
            _saved = saved;
            _status.Text = Loc.S.JobSavedIn(FolderName(_folder));
            Root.Cursor = Cursors.Hand;
            Root.ToolTip = saved.Length == 1 ? saved[0] : _folder;
            Linger(DoneLinger);
        }

        public void Failed(string message)
        {
            Finish(ErrorBrush, "!");
            _status.Text = message;
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
                else Process.Start("explorer.exe", $"\"{_folder}\"");
            }
            catch (Exception ex) { Log.Write("Open saved file: " + ex.Message); }
        }

        private static string FolderName(string folder) =>
            string.Equals(folder.TrimEnd('\\'), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), StringComparison.OrdinalIgnoreCase)
                ? Loc.S.Desktop
                : Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } name ? name : folder;
    }
}
