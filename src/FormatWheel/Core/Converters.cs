using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FormatWheel.Sdk;

namespace FormatWheel;

// Реестр конвертеров: встроенные картинки + загруженные из DLL-расширений.
internal static class Converters
{
    private static IReadOnlyList<IConverter> _all = Array.Empty<IConverter>();

    public static void Set(IEnumerable<IConverter> converters) => _all = converters.ToList();

    // первый, кто берёт ВСЕ файлы. Картинки идут первыми — расширением не перебить.
    public static IConverter? For(IReadOnlyList<string> files) =>
        _all.FirstOrDefault(c => files.All(c.CanHandle));
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
