using System;
using System.IO;

namespace FormatWheel;

internal static class Paths
{
    public static string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "FormatWheel");
    public static string AppData { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FormatWheel");

    // расширения-DLL: встроенные рядом с exe, пользовательские в AppData
    public static string UserExtensions => Path.Combine(AppData, "extensions");
    public static string BundledExtensions => Path.Combine(AppContext.BaseDirectory, "extensions");

    // Сразу после броска удалять нельзя — получатель может читать файл позже (чат, например).
    // Поэтому чистим старьё при запуске.
    public static void CleanupOld(TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (string dir in Directory.EnumerateDirectories(TempRoot))
            {
                try
                {
                    if (DateTime.Now - Directory.GetCreationTime(dir) > maxAge)
                        Directory.Delete(dir, recursive: true);
                }
                catch { }
            }
        }
        catch { }
    }
}
