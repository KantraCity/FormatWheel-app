using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FormatWheel;

// Magick.NET HEIC только читает — пишущий HEVC-кодек нельзя таскать с собой из-за лицензий.
// Зато винда умеет писать HEIC сама, если стоят «HEIF Image Extensions» + «HEVC Video Extensions» из Store.
internal static class WicHeif
{
    private static readonly Guid HeifContainer = new("E1E62521-6787-405B-A339-500715B5763F"); // GUID_ContainerFormatHeif

    public static bool IsAvailable()
    {
        try
        {
            bool ok = false;
            Sta.Run(() =>
            {
                const int size = 64;
                var pixels = new byte[size * size * 4];
                var source = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgr32, null, pixels, size * 4);
                using var stream = new MemoryStream();
                Encode(source, stream);
                ok = stream.Length > 0;
            });
            return ok;
        }
        catch { return false; }
    }

    // на вход PNG, который уже подготовил Magick (поворот, фон, размер)
    public static void SaveFromPng(byte[] png, string path)
    {
        Sta.Run(() =>
        {
            using var input = new MemoryStream(png);
            var frame = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource source = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
            using var output = File.Create(path);
            Encode(source, output);
        });
    }

    private static void Encode(BitmapSource source, Stream output)
    {
        var encoder = BitmapEncoder.Create(HeifContainer);
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(output);
    }
}
