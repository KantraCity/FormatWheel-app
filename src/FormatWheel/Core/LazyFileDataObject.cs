using System;
using System.IO;
using System.Windows;

namespace FormatWheel;

// Файлы отдаём только когда получатель попросит. Ничего не кэшируем:
// пока файл тащат, выбор на слотах можно поменять ещё раз.
// ready = false — файла ещё нет (долгая конвертация, видео): говорим, что файлов нет вовсе,
// иначе получатель зависнет в ожидании на минуты.
internal sealed class LazyFileDataObject : IDataObject
{
    private const string PreferredDropEffect = "Preferred DropEffect";

    private readonly Func<string[]> _files;
    private readonly Func<bool> _ready;

    public LazyFileDataObject(Func<string[]> files, Func<bool> ready)
    {
        _files = files;
        _ready = ready;
    }

    public object? GetData(string format) => GetData(format, true);

    public object? GetData(string format, bool autoConvert)
    {
        if (format == DataFormats.FileDrop) return _ready() ? _files() : null;
        // без этого Проводник при броске в папку может решить переместить файл, а не скопировать
        if (format == PreferredDropEffect) return new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy));
        return null;
    }

    public object? GetData(Type format) => format == typeof(string[]) && _ready() ? _files() : null;

    public bool GetDataPresent(string format) => format == DataFormats.FileDrop ? _ready() : format == PreferredDropEffect;
    public bool GetDataPresent(string format, bool autoConvert) => GetDataPresent(format);
    public bool GetDataPresent(Type format) => format == typeof(string[]) && _ready();

    public string[] GetFormats() => _ready() ? new[] { DataFormats.FileDrop, PreferredDropEffect } : new[] { PreferredDropEffect };
    public string[] GetFormats(bool autoConvert) => GetFormats();

    public void SetData(object data) { }
    public void SetData(string format, object data) { }
    public void SetData(Type format, object data) { }
    public void SetData(string format, object data, bool autoConvert) { }
}
