using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace FormatWheel;

// Сначала берём миниатюру у Проводника (она в кэше, приходит мгновенно, даже для HEIC/RAW),
// если не дал — делаем сами.
internal static class Thumbnails
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    private const int SIIGBF_THUMBNAILONLY = 0x8;

    public static Task<BitmapSource?> GetAsync(string path, int size) => Sta.RunAsync(() => Get(path, size));

    private static BitmapSource? Get(string path, int size)
    {
        try
        {
            var shell = FromShell(path, size);
            if (shell != null) return shell;
        }
        catch (Exception ex) { Log.Write("Shell thumbnail failed: " + ex.Message); }

        try { return FromMagick(path, size); }
        catch (Exception ex) { Log.Write("Thumbnail failed: " + ex.Message); return null; }
    }

    private static BitmapSource? FromShell(string path, int size)
    {
        SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
        try
        {
            int hr = factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_THUMBNAILONLY, out IntPtr hbitmap);
            if (hr != 0 || hbitmap == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { Native.DeleteObject(hbitmap); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    private static BitmapSource FromMagick(string path, int size)
    {
        using var image = new MagickImage();
        image.Settings.SetDefine(MagickFormat.Jpeg, "size", $"{size}x{size}"); // JPEG сразу декодится уменьшенным, быстрее
        image.Read(path);
        image.AutoOrient();
        image.Thumbnail(new MagickGeometry((uint)size, (uint)size));

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(image.ToByteArray(MagickFormat.Png));
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
