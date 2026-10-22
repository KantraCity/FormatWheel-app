using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace FormatWheel.Sdk;

// Действия над файлами вместо конвертации: печать, отправка, открыть в программе, загрузить куда-то.
// В отличие от IConverter, действие ничего не возвращает: выбрали лепесток — драг Проводника отменяется,
// а действие выполняется в фоне (прогресс и отмена — в панели у трея).
// Расширение может реализовать IWheelActions вместо IConverter или вместе с ним (SDK 1.1+).
public interface IWheelActions
{
    // человекочитаемое имя, видно в меню расширений в трее
    string Name { get; }

    // true — для файлов этого типа у нас есть действия
    bool CanHandle(string path);

    // действия для этих файлов; лепестки встают в основное меню (Shift) после форматов
    IReadOnlyList<WheelAction> ActionsFor(IReadOnlyList<string> files);
}

// Один лепесток-действие.
// Run(файлы, report(0..1), ct) вызывается в отдельном STA-потоке: из него можно показывать окна
// (например, диалог печати). Файлы — оригиналы, которые тащили.
public sealed record WheelAction(
    string Label,
    string Hint,
    Func<IReadOnlyList<string>, Action<double>, CancellationToken, Task> Run,
    Icon? Icon = null);
