using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace FormatWheel;

// Крошечное невидимое окно под курсором. В него прилетает имитированный клик, после чего мышь «наша»
// и отсюда стартует свой драг. Вынесено из колеса, чтобы оно спокойно доигрывало анимацию закрытия.
internal sealed class GrabTrapWindow : Window
{
    private const double Size = 64;
    private readonly IntPtr _hwnd;
    private bool _armed;

    public event Action? Grabbed;

    public GrabTrapWindow()
    {
        Width = Size;
        Height = Size;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        AllowDrop = false;

        var brush = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));
        brush.Freeze();
        Background = brush;

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.SetExStyle(_hwnd, add: Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW, remove: 0);

        PreviewMouseLeftButtonDown += OnMouseDown;
    }

    public void ArmAt(Native.POINT cursor)
    {
        Show();
        Native.GetWindowRect(_hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, cursor.X - w / 2, cursor.Y - h / 2, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        _armed = true;
    }

    public void Disarm()
    {
        _armed = false;
        Hide();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_armed) return;
        _armed = false;
        e.Handled = true;
        Grabbed?.Invoke();
    }
}
