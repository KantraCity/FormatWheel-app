using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FormatWheel;

// Какая папка под курсором: окно Проводника или рабочий стол. Нужно, когда долгую конвертацию
// бросили раньше, чем она закончилась: файл докладываем туда сами.
internal static class ShellTarget
{
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    private const uint GA_ROOT = 2;

    // null — под курсором не папка (чат, браузер, пустое место)
    public static string? FolderAt(Native.POINT point)
    {
        try
        {
            IntPtr root = GetAncestor(WindowFromPoint(point), GA_ROOT);
            if (root == IntPtr.Zero) return null;

            string cls = Read(GetClassName, root);
            if (cls is "Progman" or "WorkerW")
                return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (cls != "CabinetWClass") return null;

            // во вкладках Windows 11 у всех вкладок один HWND — активную узнаём по заголовку окна
            string title = Read(GetWindowText, root);
            string? first = null;
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            foreach (dynamic window in shell.Windows())
            {
                try
                {
                    if ((long)window.HWND != (long)root) continue;
                    string? path = window.Document?.Folder?.Self?.Path as string;
                    if (string.IsNullOrEmpty(path) || !System.IO.Directory.Exists(path)) continue;
                    first ??= path;
                    string name = window.LocationName as string ?? "";
                    if (name.Length > 0 && title.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return path;
                }
                catch { }
            }
            return first;
        }
        catch (Exception ex)
        {
            Log.Write("Folder under cursor: " + ex.Message);
            return null;
        }
    }

    private static string Read(Func<IntPtr, StringBuilder, int, int> get, IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        get(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
