using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FormatWheel.Sdk;
using ImageMagick;

namespace FormatWheel;

// Встроенный конвертер картинок на Magick.NET.
// Слот Shift — форматы, слот Ctrl — размеры. Alt-слоты пустые (это и есть «свои слоты» для картинок).
internal sealed class ImageConverter : IConverter
{
    private const string AxisFormat = "image.format";
    private const string AxisSize = "image.size";

    public string Name => "Images";
    public bool IsSlow => false;
    public int MaxParallel => Math.Max(1, Environment.ProcessorCount / 2);

    public bool CanHandle(string path) => Formats.IsSupportedInput(path);

    public IReadOnlyList<WheelOption> MenuFor(WheelSlot slot, IReadOnlyList<string> files)
    {
        if (slot == WheelSlot.Primary)
            return Formats.Wheel
                .Select(f => new WheelOption(AxisFormat, f, f.Label, f.Hint))
                .ToList();

        if (slot == WheelSlot.Ctrl)
        {
            var dims = files.Select(Resolutions.Probe).Where(d => d != null).Select(d => d!).ToList();
            var menu = dims.Count > 0 ? Resolutions.Build(dims) : null;
            if (menu == null) return Array.Empty<WheelOption>();

            return menu.Options.Select(o => o.IsOriginal
                ? WheelOption.Reset(AxisSize, o.Label, o.Hint)
                : new WheelOption(AxisSize, o, o.Label, o.Hint)).ToList();
        }

        return Array.Empty<WheelOption>(); // Alt, CtrlAlt — у картинок пусто
    }

    public string Convert(string source, Recipe recipe, Action<double> report, CancellationToken ct)
    {
        var format = recipe.Get<WheelFormat>(AxisFormat) ?? Formats.KeepFormatFor(source);
        var size = recipe.Get<SizeOption>(AxisSize);

        using var image = new MagickImage();
        image.Progress += (_, e) =>
        {
            if (ct.IsCancellationRequested) { e.Cancel = true; return; }
            double p = Math.Clamp(e.Progress.ToDouble() / 100.0, 0, 1);
            string origin = e.Origin ?? "";
            double mapped =
                origin.StartsWith("Load", StringComparison.OrdinalIgnoreCase) ? p * 0.45 :
                origin.StartsWith("Save", StringComparison.OrdinalIgnoreCase) ? 0.55 + p * 0.44 : 0.45 + p * 0.10;
            report(mapped);
        };

        image.Read(source);
        ct.ThrowIfCancellationRequested();
        report(0.45);
        image.AutoOrient(); // фото с телефона иначе лягут на бок

        bool resized = false;
        if (size is { IsOriginal: false })
        {
            int longSide = (int)Math.Max(image.Width, image.Height);
            int targetLong = size.Scale > 0 ? Math.Max(1, (int)Math.Round(longSide * size.Scale)) : Math.Max(size.Width, size.Height);
            if (longSide > targetLong) // только уменьшаем
            {
                // Exact: пропорции совпали со стандартом — подгоняем ровно, растяжение на глаз не видно.
                // Иначе вписываем по длинной стороне, пропорции не трогаем. Обрезки нет.
                var geometry = size.Exact && size.Scale <= 0
                    ? new MagickGeometry((uint)size.Width, (uint)size.Height) { IgnoreAspectRatio = true }
                    : new MagickGeometry((uint)targetLong, (uint)targetLong);
                image.Resize(geometry);
                resized = true;
            }
        }

        if (!format.SupportsAlpha && image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White; // иначе прозрачность в JPG станет чёрной
            image.Alpha(AlphaOption.Remove);
        }
        if (format.Magick == MagickFormat.Ico && (image.Width > 256 || image.Height > 256))
            image.Resize(new MagickGeometry(256, 256));
        if (format.Magick == MagickFormat.Tiff)
            image.Settings.Compression = CompressionMethod.LZW; // несжатый TIFF из фотки — сотни мегабайт
        if (format.Quality > 0)
            image.Quality = format.Quality;

        ct.ThrowIfCancellationRequested();
        report(0.55);

        string output = OutputPath.New(source, format.Extension, resized ? $"_{image.Width}x{image.Height}" : "");
        if (format.UseWic)
            WicHeif.SaveFromPng(image.ToByteArray(MagickFormat.Png), output);
        else
            image.Write(output, format.Magick);

        report(1.0);
        return output;
    }
}
