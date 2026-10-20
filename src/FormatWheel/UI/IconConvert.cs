using System;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FormatWheel;

// System.Drawing.Icon (его отдают расширения на лепестках) -> WPF ImageSource
internal static class IconConvert
{
    public static BitmapSource? ToSource(Icon? icon)
    {
        if (icon == null) return null;
        try
        {
            using var bitmap = icon.ToBitmap();
            IntPtr hbitmap = bitmap.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                Native.DeleteObject(hbitmap);
            }
        }
        catch
        {
            return null;
        }
    }
}
