using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FormatWheel.Sdk;

namespace FormatWheel;

// Реестр конвертеров (встроенные картинки + из DLL-расширений) и действий из расширений.
internal static class Converters
{
    private static IReadOnlyList<IConverter> _all = Array.Empty<IConverter>();
    private static IReadOnlyList<IWheelActions> _actions = Array.Empty<IWheelActions>();

    public static void Set(IEnumerable<IConverter> converters, IEnumerable<IWheelActions> actions)
    {
        _all = converters.ToList();
        _actions = actions.ToList();
    }

    // первый, кто берёт ВСЕ файлы. Картинки идут первыми — расширением не перебить.
    public static IConverter? For(IReadOnlyList<string> files) =>
        _all.FirstOrDefault(c => files.All(c.CanHandle));

    // действия всех расширений, которые берут ВСЕ файлы; ошибка одного расширения не мешает остальным
    public static IReadOnlyList<WheelAction> ActionsFor(IReadOnlyList<string> files)
    {
        var result = new List<WheelAction>();
        foreach (var provider in _actions)
        {
            try
            {
                if (files.All(provider.CanHandle)) result.AddRange(provider.ActionsFor(files));
            }
            catch (Exception ex) { Log.Write($"Actions of {provider.Name}: {ex.Message}"); }
        }
        return result;
    }

    public static bool HasActionsFor(IReadOnlyList<string> files) => _actions.Any(p => files.All(f => Safe(() => p.CanHandle(f))));

    private static bool Safe(Func<bool> check) { try { return check(); } catch { return false; } }
}

// гоняет конвертацию по всем файлам с общим прогрессом и отменой
internal static class ConversionRunner
{
    public static string[] ConvertAll(IConverter converter, string[] files, Recipe recipe, IProgress<double>? progress, CancellationToken ct)
    {
        var results = new string[files.Length];
        var parts = new double[files.Length];
        var gate = new object();
        double lastReported = -1;

        void Report(int index, double value)
        {
            lock (gate)
            {
                if (value <= parts[index]) return;
                parts[index] = value;
                double total = parts.Average();
                if (total - lastReported < 0.01 && total < 1) return; // не спамим UI каждой строчкой
                lastReported = total;
                progress?.Report(total);
            }
        }

        var options = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, converter.MaxParallel), CancellationToken = ct };
        System.Threading.Tasks.Parallel.For(0, files.Length, options, i =>
        {
            // ниже обычного: конвертация не должна отнимать процессор у драга и анимаций
            var thread = Thread.CurrentThread;
            var priority = thread.Priority;
            thread.Priority = ThreadPriority.BelowNormal;
            try { results[i] = converter.Convert(files[i], recipe, v => Report(i, v), ct); }
            finally { thread.Priority = priority; }
        });
        return results;
    }
}
