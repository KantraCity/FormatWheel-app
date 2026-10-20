using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FormatWheel;

// Глобальный хук клавиатуры в своём потоке со своим циклом сообщений:
// на UI-потоке винда снимает хук по таймауту, стоит UI задуматься.
internal sealed class KeyboardHook : IDisposable
{
    // все события приходят из потока хука, не из UI
    public event Action<bool>? ShiftChanged;
    public event Action<bool>? CtrlChanged;
    public event Action<bool>? AltChanged;

    private Native.LowLevelProc? _proc; // держим ссылку, иначе GC соберёт делегат
    private IntPtr _hook;
    private uint _threadId;
    private bool _shift, _ctrl, _alt;

    public void Start()
    {
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            _proc = HookProc;
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
            ready.Set();
            if (_hook == IntPtr.Zero) return;
            while (Native.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            Native.UnhookWindowsHookEx(_hook);
        })
        { IsBackground = true, Name = "FormatWheel keyboard hook" };
        thread.Start();
        ready.Wait();

        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"Keyboard hook failed (error {Marshal.GetLastWin32Error()}).");
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            bool injected = (data.flags & Native.LLKHF_INJECTED) != 0; // свой Esc не считаем
            if (!injected)
            {
                int msg = (int)wParam;
                bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;

                if (data.vkCode is Native.VK_SHIFT or Native.VK_LSHIFT or Native.VK_RSHIFT)
                    Track(ref _shift, down, up, ShiftChanged);
                else if (data.vkCode is Native.VK_CONTROL or Native.VK_LCONTROL or Native.VK_RCONTROL)
                    Track(ref _ctrl, down, up, CtrlChanged);
                else if (data.vkCode is Native.VK_MENU or Native.VK_LMENU or Native.VK_RMENU)
                    Track(ref _alt, down, up, AltChanged);
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void Track(ref bool state, bool down, bool up, Action<bool>? changed)
    {
        // автоповтор при удержании шлёт кучу KEYDOWN, нужен только первый
        if (down && !state) { state = true; changed?.Invoke(true); }
        else if (up && state) { state = false; changed?.Invoke(false); }
    }

    public void Dispose()
    {
        if (_threadId != 0)
            Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
