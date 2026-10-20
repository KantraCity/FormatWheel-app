using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FormatWheel;

internal static class Paths
{
    public static string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "FormatWheel");
    public static string AppData { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FormatWheel");

    // расширения-DLL: встроенные рядом с exe, пользовательские в AppData
    public static string UserExtensions => Path.Combine(AppData, "extensions");
    public static string BundledExtensions => Path.Combine(AppContext.BaseDirectory, "extensions");

    // Папка копий: у каждого сконвертированного файла своя подпапка.
    // Сразу после броска удалять нельзя — получатель может читать файл позже (чат при отправке, например).
    // Поэтому: всё старше maxAge удаляем, а если копий набралось больше maxBytes — убираем самые старые,
    // пока не уложимся. Свежее minAge не трогаем никогда: это может быть файл, который прямо сейчас тащат,
    // отправляют или ещё кодируют. Возраст — по последней записи в папке, а не по созданию:
    // папку долгого кодирования видео создали давно, но пишут в неё до сих пор.
    public static void Cleanup(TimeSpan maxAge, long maxBytes, TimeSpan minAge)
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            var now = DateTime.Now;
            var folders = new List<(string Path, DateTime Touched, long Bytes)>();
            foreach (string dir in Directory.EnumerateDirectories(TempRoot))
            {
                try
                {
                    var files = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
                    var touched = files.Count > 0 ? files.Max(f => f.LastWriteTime) : Directory.GetLastWriteTime(dir);
                    folders.Add((dir, touched, files.Sum(f => f.Length)));
                }
                catch { }
            }

            int deleted = 0;
            long freed = 0, total = folders.Sum(f => f.Bytes);
            foreach (var folder in folders.OrderBy(f => f.Touched))
            {
                var age = now - folder.Touched;
                if (age < minAge) break; // дальше только свежее
                if (age <= maxAge && total <= maxBytes) break; // и не старые, и место есть
                try
                {
                    Directory.Delete(folder.Path, recursive: true);
                    deleted++;
                    freed += folder.Bytes;
                    total -= folder.Bytes;
                }
                catch { } // файл ещё открыт получателем — попробуем в следующий раз
            }
            if (deleted > 0)
                Log.Write($"Cleanup: removed {deleted} old copies, {freed / (1024 * 1024)} MB; left {total / (1024 * 1024)} MB");
        }
        catch (Exception ex) { Log.Write("Cleanup failed: " + ex.Message); }
    }
}
