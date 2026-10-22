using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FormatWheel;

// Напоминалки в трее во время обычного драга: человек тащит файл, который мы умеем конвертировать, а Shift не жмёт.
// Первый такой драг после запуска — напоминаем обязательно, дальше — с шансом 10%.
// Клик по напоминалке выключает их навсегда (файл-флаг в AppData).
//
// Свой драг Проводника мы не видим, пока не открыто колесо. Поэтому раз в ~120 мс смотрим: кнопка мыши зажата
// и на экране окно-картинка драга Проводника (SysDragImage) — значит, что-то тащат. Что именно — выделение
// в окне, из которого тащат (оно на переднем плане).
internal sealed class DragTips
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    private const double Chance = 0.10;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    private static string OffFlag => Path.Combine(Paths.AppData, "drag-tips-off");

    private readonly Func<bool> _idle;
    private readonly Action<string, string> _show;
    private readonly DispatcherTimer _timer;
    private readonly Random _random = new();
    private bool _seen;        // этот драг уже разобрали
    private bool _shownOnce;   // первая напоминалка после запуска уже была
    private int _lastPhrase = -1;

    public static bool Enabled => !File.Exists(OffFlag);

    // idle — колесо закрыто и своего драга нет; show(заголовок, текст) — показать напоминалку в трее
    public DragTips(Func<bool> idle, Action<string, string> show)
    {
        _idle = idle;
        _show = show;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => Tick();
        if (Enabled) _timer.Start();
    }

    public void TurnOff()
    {
        _timer.Stop();
        try { Directory.CreateDirectory(Paths.AppData); File.WriteAllText(OffFlag, ""); }
        catch (Exception ex) { Log.Write("Drag tips off: " + ex.Message); }
        Log.Write("Drag tips turned off");
    }

    private void Tick()
    {
        if (!Native.PrimaryButtonDown()) { _seen = false; return; }
        if (_seen || !_idle() || !ExplorerDragging()) return;
        _seen = true;
        _ = CheckAsync(GetForegroundWindow());
    }

    private async Task CheckAsync(IntPtr source)
    {
        try
        {
            // шанс решаем заранее: незачем дёргать Проводник, если напоминать всё равно не будем
            if (_shownOnce && _random.NextDouble() >= Chance) { Log.Write("Explorer drag: no tip this time"); return; }

            var read = Sta.RunAsync(() => ShellTarget.SelectedFiles(source));
            if (await Task.WhenAny(read, Task.Delay(ReadTimeout)) != read) { Log.Write("Explorer drag: selection read timed out"); return; }
            string[]? files = read.Result;
            if (files == null || Converters.For(files) == null)
            {
                Log.Write("Explorer drag, no tip: " + (files == null ? "no selected files" : "not convertible: " + string.Join(" | ", Array.ConvertAll(files, Path.GetFileName))));
                return;
            }
            if (!_idle() || !Enabled) return; // пока читали, человек уже открыл колесо

            _shownOnce = true;
            var phrases = Loc.S.DragTips;
            int i;
            do i = _random.Next(phrases.Length); while (phrases.Length > 1 && i == _lastPhrase);
            _lastPhrase = i;
            Log.Write("Drag tip: " + string.Join(" | ", Array.ConvertAll(files, Path.GetFileName)));
            _show(phrases[i], Loc.S.DragTipText);
        }
        catch (Exception ex) { Log.Write("Drag tip: " + ex.Message); }
    }

    // окон SysDragImage бывает несколько (по одному на процесс), и после драга они остаются скрытыми
    private static bool ExplorerDragging()
    {
        for (IntPtr w = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "SysDragImage", null); w != IntPtr.Zero;
             w = FindWindowEx(IntPtr.Zero, w, "SysDragImage", null))
            if (IsWindowVisible(w)) return true;
        return false;
    }
}
