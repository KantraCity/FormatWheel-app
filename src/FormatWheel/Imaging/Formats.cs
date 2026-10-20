using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImageMagick;

namespace FormatWheel;

// Quality = 0 — настройка кодека по умолчанию. UseWic — писать системным кодеком (HEIC, см. WicHeif).
internal sealed record WheelFormat(string Label, string Hint, string Extension, MagickFormat Magick, uint Quality, bool SupportsAlpha, bool UseWic = false);

internal static class Formats
{
    private static readonly WheelFormat[] Candidates =
    {
        new("JPG",  Loc.S.HintJpg,  "jpg",  MagickFormat.Jpeg, 90, false),
        new("PNG",  Loc.S.HintPng,  "png",  MagickFormat.Png,  0,  true),
        new("HEIC", Loc.S.HintHeic, "heic", MagickFormat.Heic, 0,  false, UseWic: true),
        new("WEBP", Loc.S.HintWebp, "webp", MagickFormat.WebP, 90, true),
        new("AVIF", Loc.S.HintAvif, "avif", MagickFormat.Avif, 70, true),
        new("JXL",  Loc.S.HintJxl,  "jxl",  MagickFormat.Jxl,  90, true),
        new("TIFF", Loc.S.HintTiff, "tif",  MagickFormat.Tiff, 0,  true),
        new("GIF",  Loc.S.HintGif,  "gif",  MagickFormat.Gif,  0,  true),
        new("BMP",  Loc.S.HintBmp,  "bmp",  MagickFormat.Bmp,  0,  false),
        new("ICO",  Loc.S.HintIco,  "ico",  MagickFormat.Ico,  0,  true),
    };

    private static readonly Dictionary<string, MagickFormat> InputTable = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = MagickFormat.Jpeg, [".jpeg"] = MagickFormat.Jpeg, [".jpe"] = MagickFormat.Jpeg, [".jfif"] = MagickFormat.Jpeg,
        [".png"] = MagickFormat.Png, [".webp"] = MagickFormat.WebP, [".avif"] = MagickFormat.Avif, [".jxl"] = MagickFormat.Jxl,
        [".gif"] = MagickFormat.Gif, [".bmp"] = MagickFormat.Bmp, [".dib"] = MagickFormat.Bmp,
        [".tif"] = MagickFormat.Tiff, [".tiff"] = MagickFormat.Tiff, [".ico"] = MagickFormat.Ico,
        [".jp2"] = MagickFormat.Jp2, [".tga"] = MagickFormat.Tga, [".psd"] = MagickFormat.Psd,
        // iPhone: HEIC и ProRAW (обычный DNG)
        [".heic"] = MagickFormat.Heic, [".heif"] = MagickFormat.Heic, [".hif"] = MagickFormat.Heic, [".dng"] = MagickFormat.Dng,
        // RAW с камер через LibRaw
        [".cr2"] = MagickFormat.Cr2, [".cr3"] = MagickFormat.Cr3, [".nef"] = MagickFormat.Nef, [".arw"] = MagickFormat.Arw,
        [".raf"] = MagickFormat.Raf, [".orf"] = MagickFormat.Orf, [".rw2"] = MagickFormat.Rw2, [".pef"] = MagickFormat.Pef,
    };

    public static WheelFormat[] Wheel { get; private set; } = Array.Empty<WheelFormat>();
    private static HashSet<string> _readable = new(StringComparer.OrdinalIgnoreCase);

    public static bool HeicHidden => Wheel.All(f => !f.UseWic);

    // не верим списку на слово — спрашиваем у Magick.NET и винды, что реально работает на этой машине
    public static void Initialize()
    {
        bool heic = WicHeif.IsAvailable();
        Wheel = Candidates.Where(f => f.UseWic ? heic : CanWrite(f.Magick)).ToArray();
        _readable = InputTable.Where(kv => CanRead(kv.Value)).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Log.Write("Image formats: " + string.Join(", ", Wheel.Select(f => f.Label)) + (heic ? "" : " (HEIC hidden)"));
    }

    public static bool IsSupportedInput(string path) => File.Exists(path) && _readable.Contains(Path.GetExtension(path));

    // когда поменяли только размер; RAW/PSD/HEIC-без-кодека в свой формат не пишутся — тогда JPG
    public static WheelFormat KeepFormatFor(string path)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant() switch
        {
            "jpeg" or "jpe" or "jfif" => "jpg",
            "tiff" => "tif",
            "heif" or "hif" => "heic",
            var other => other
        };
        return Wheel.FirstOrDefault(f => f.Extension == ext) ?? Wheel.First(f => f.Magick == MagickFormat.Jpeg);
    }

    private static bool CanWrite(MagickFormat f) { try { return MagickFormatInfo.Create(f)?.SupportsWriting == true; } catch { return false; } }
    private static bool CanRead(MagickFormat f) { try { return MagickFormatInfo.Create(f)?.SupportsReading == true; } catch { return false; } }
}
