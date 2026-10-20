using System;
using System.Collections.Generic;
using System.Threading;

namespace FormatWheel.Sdk;

// Главный контракт расширения. Пишете класс, реализующий IConverter, собираете в DLL,
// кладёте рядом с программой — и колесо начинает конвертировать ваши файлы.
// Подробности и примеры — в README главного репозитория FormatWheel.
public interface IConverter
{
    // человекочитаемое имя, видно в меню расширений в трее
    string Name { get; }

    // true — файл этого типа умеем обрабатывать
    bool CanHandle(string path);

    // Слоты колеса: Shift, Ctrl+Shift, Alt+Shift, Ctrl+Alt+Shift.
    // Для каждого вернуть лепестки или пусто. Набор может зависеть от конкретных файлов.
    IReadOnlyList<WheelOption> MenuFor(WheelSlot slot, IReadOnlyList<string> files);

    // Долгая конвертация (видео): файл отдаём получателю только когда готов,
    // а не вешаем его ожиданием на минуты. Для картинок false.
    bool IsSlow { get; }

    // сколько файлов конвертировать параллельно
    int MaxParallel { get; }

    // report(0..1) — прогресс этого файла; вернуть путь к готовому файлу.
    // recipe — что выбрано на всех слотах; ct — отмена (передумали посреди драга).
    string Convert(string source, Recipe recipe, Action<double> report, CancellationToken ct);
}

// Слоты по модификаторам поверх Shift
public enum WheelSlot
{
    Primary,      // Shift
    Ctrl,         // Ctrl+Shift
    Alt,          // Alt+Shift
    CtrlAlt       // Ctrl+Alt+Shift
}
