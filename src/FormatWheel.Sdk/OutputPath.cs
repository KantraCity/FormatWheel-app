using System;
using System.IO;

namespace FormatWheel.Sdk;

// Куда писать результат. Программа задаёт временную папку, расширение просто просит имя файла.
public static class OutputPath
{
    // программа выставляет это при запуске
    public static string TempRoot { get; set; } = Path.Combine(Path.GetTempPath(), "FormatWheel");

    // своя папка на каждый файл, имя как у оригинала — в чате/браузере так приятнее
    public static string New(string source, string extension, string suffix = "")
    {
        string dir = Path.Combine(TempRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(source) + suffix + "." + extension.TrimStart('.'));
    }
}
