using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImageMagick;

namespace FormatWheel;

// размер уже с учётом поворота из EXIF — как картинку видит человек
internal sealed record ImageDims(int Width, int Height)
{
    public int Long => Math.Max(Width, Height);
    public int Short => Math.Min(Width, Height);
    public bool Portrait => Height > Width;
}

// Exact — привести ровно к Width×Height (пропорции совпали со стандартом).
// Scale > 0 — уменьшить в долях от исходника. IsOriginal — лепесток «не менять».
internal sealed record SizeOption(string Label, string Hint, int Width, int Height, bool Exact, double Scale = 0)
{
    public bool IsOriginal => Width == 0 && Scale <= 0;
    public static SizeOption Original { get; } = new(Loc.S.Original, Loc.S.Unchanged, 0, 0, false);
}

internal sealed record SizeMenu(string Description, IReadOnlyList<SizeOption> Options);

internal static class Resolutions
{
    private const double Tolerance = 0.015; // насколько пропорции могут отличаться от стандарта

    private sealed record Standard(string Name, int RatioW, int RatioH, (int W, int H, string Hint)[] Sizes)
    {
        public double Ratio => (double)RatioW / RatioH;
    }

    private static readonly Standard[] Standards =
    {
        new("16:9", 16, 9, new[] { (3840, 2160, "4K"), (2560, 1440, "2K QHD"), (1920, 1080, "Full HD"), (1280, 720, "HD"), (854, 480, "480p") }),
        new("4:3", 4, 3, new[] { (4032, 3024, Loc.S.Megapixels(12)), (2048, 1536, Loc.S.Megapixels(3)), (1600, 1200, Loc.S.Megapixels(2)), (1024, 768, Loc.S.SizeEmail), (640, 480, Loc.S.SizePreview) }),
        new("3:2", 3, 2, new[] { (6000, 4000, Loc.S.Megapixels(24)), (3000, 2000, Loc.S.Megapixels(6)), (1800, 1200, Loc.S.SizePrint), (1200, 800, Loc.S.SizeWeb), (600, 400, Loc.S.SizePreview) }),
        new("1:1", 1, 1, new[] { (2048, 2048, Loc.S.SizeLarge), (1080, 1080, "Instagram"), (1024, 1024, Loc.S.SizeMedium), (512, 512, Loc.S.SizeAvatar), (256, 256, Loc.S.SizeIcon) }),
        new("5:4", 5, 4, new[] { (2560, 2048, Loc.S.Megapixels(5)), (1280, 1024, "SXGA"), (1000, 800, Loc.S.SizeWeb), (640, 512, Loc.S.SizePreview) }),
        new("21:9", 21, 9, new[] { (5120, 2160, "5K2K"), (3440, 1440, "UWQHD"), (2560, 1080, "UW Full HD"), (1720, 720, "UW HD") }),
    };

    private static readonly (int Edge, string Hint)[] FreeEdges =
    {
        (4096, "4K"), (2560, Loc.S.SizeLarge), (1920, "Full HD"), (1280, "HD"), (800, Loc.S.SizeEmail), (400, Loc.S.SizePreview)
    };

    public static ImageDims? Probe(string path)
    {
        var dims = TryProbe(path, false) ?? TryProbe(path, true); // Ping быстрый, но часть RAW/HEIC так не отдаёт размер
        Log.Write(dims != null ? $"Size of {Path.GetFileName(path)}: {dims.Width}x{dims.Height}" : $"Size unknown: {path}");
        return dims;
    }

    private static ImageDims? TryProbe(string path, bool fullRead)
    {
        try
        {
            using var image = new MagickImage();
            if (fullRead) image.Read(path); else image.Ping(path);
            int w = (int)image.Width, h = (int)image.Height;
            if (image.Orientation is OrientationType.LeftTop or OrientationType.RightTop or OrientationType.RightBottom or OrientationType.LeftBottom)
                (w, h) = (h, w);
            return w > 0 && h > 0 ? new ImageDims(w, h) : null;
        }
        catch (Exception ex) { Log.Write($"Probe ({(fullRead ? "read" : "ping")}) failed for {path}: {ex.Message}"); return null; }
    }

    public static SizeMenu? Build(IReadOnlyList<ImageDims> dims)
    {
        if (dims.Count == 0) return null;

        int maxLong = dims.Max(d => d.Long);
        bool portrait = dims[0].Portrait;
        bool sameOrientation = dims.All(d => d.Portrait == portrait);

        Standard? standard = sameOrientation ? Match(dims[0]) : null;
        if (standard != null && dims.Any(d => Match(d) != standard)) standard = null;

        var options = new List<SizeOption>();
        string description;

        if (standard != null)
        {
            foreach (var (w, h, hint) in standard.Sizes)
            {
                if (w >= maxLong) continue;
                options.Add(portrait ? new SizeOption($"{h}×{w}", hint, h, w, true) : new SizeOption($"{w}×{h}", hint, w, h, true));
            }
            string ratio = portrait ? $"{standard.RatioH}:{standard.RatioW}" : standard.Name;
            description = dims.Count == 1 ? $"{dims[0].Width}×{dims[0].Height} · {ratio}" : Loc.S.RatioOf(ratio);
        }
        else
        {
            foreach (var (edge, hint) in FreeEdges)
            {
                if (edge >= maxLong) continue;
                if (dims.Count == 1)
                {
                    var d = dims[0];
                    int shortSide = (int)Math.Round(edge * (double)d.Short / d.Long);
                    options.Add(d.Portrait ? new SizeOption($"{shortSide}×{edge}", hint, shortSide, edge, false) : new SizeOption($"{edge}×{shortSide}", hint, edge, shortSide, false));
                }
                else options.Add(new SizeOption($"≤ {edge}", Loc.S.LongSide, edge, edge, false));
            }
            description = dims.Count == 1 ? $"{dims[0].Width}×{dims[0].Height} · {Loc.S.OwnRatio}" : Loc.S.MixedRatios;
        }

        // проценты — для мелких картинок, где стандартные размеры все больше самой картинки
        if (options.Count > 6) options = options.Take(6).ToList();
        int minLong = dims.Min(d => d.Long);
        foreach (double scale in new[] { 0.75, 0.5, 0.25 })
        {
            if (Math.Round(minLong * scale) < 16) continue;
            string hint = dims.Count == 1 ? $"{(int)Math.Round(dims[0].Width * scale)}×{(int)Math.Round(dims[0].Height * scale)}" : Loc.S.EachFile;
            options.Add(new SizeOption($"{scale * 100:0}%", hint, 0, 0, false, scale));
        }

        if (options.Count == 0) return null;
        var all = new List<SizeOption> { SizeOption.Original };
        all.AddRange(options);
        return new SizeMenu(description, all);
    }

    private static Standard? Match(ImageDims d)
    {
        double ratio = (double)d.Long / d.Short;
        return Standards.FirstOrDefault(s => Math.Abs(ratio - s.Ratio) / s.Ratio <= Tolerance);
    }
}
