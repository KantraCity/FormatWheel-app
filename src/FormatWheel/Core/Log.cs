using System;
using System.IO;

namespace FormatWheel;

// %TEMP%\FormatWheel\log.txt — без него отлаживать перехват драга было бы невозможно
internal static class Log
{
    private static readonly object Gate = new();
    public static string FilePath { get; } = Path.Combine(Paths.TempRoot, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.TempRoot);
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public static void TrimOnStartup()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > 1024 * 1024) info.Delete();
        }
        catch { }
    }
}
