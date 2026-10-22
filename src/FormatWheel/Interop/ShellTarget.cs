using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FormatWheel;

// Какая папка под курсором: окно Проводника или рабочий стол. Нужно, когда долгую конвертацию
// бросили раньше, чем она закончилась: файл докладываем туда сами.
// И что выделено в окне Проводника — для подсказок во время обычного драга (DragTips).
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

    // Выделенные файлы в окне Проводника или на рабочем столе — то, что человек сейчас тащит.
    // null — окно не Проводник или выделения нет. Звать не из UI-потока: Проводник может ответить не сразу.
    public static string[]? SelectedFiles(IntPtr window)
    {
        try
        {
            IntPtr root = GetAncestor(window, GA_ROOT);
            if (root == IntPtr.Zero) return null;
            string cls = Read(GetClassName, root);
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            dynamic? view = null;

            if (cls is "Progman" or "WorkerW")
            {
                object loc = 0, empty = Type.Missing; // CSIDL_DESKTOP
                int hwnd = 0;
                view = shell.Windows().FindWindowSW(ref loc, ref empty, 8 /* SWC_DESKTOP */, ref hwnd, 1 /* SWFO_NEEDDISPATCH */)?.Document;
            }
            else if (cls == "CabinetWClass")
            {
                // вкладки Windows 11 делят один HWND — активную узнаём по заголовку, как в FolderAt
                string title = Read(GetWindowText, root);
                foreach (dynamic w in shell.Windows())
                {
                    try
                    {
                        if ((long)w.HWND != (long)root) continue;
                        view ??= w.Document;
                        string name = w.LocationName as string ?? "";
                        if (name.Length > 0 && title.StartsWith(name, StringComparison.OrdinalIgnoreCase)) { view = w.Document; break; }
                    }
                    catch { }
                }
            }
            if (view == null) return null;

            var files = new System.Collections.Generic.List<string>();
            foreach (dynamic item in view.SelectedItems())
                if (item.IsFileSystem && !item.IsFolder) files.Add((string)item.Path);
            return files.Count > 0 ? files.ToArray() : null;
        }
        catch (Exception ex)
        {
            Log.Write("Selected files: " + ex.Message);
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
