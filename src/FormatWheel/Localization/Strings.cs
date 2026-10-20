using System;
using System.Globalization;

namespace FormatWheel;

// Все строки интерфейса. required — компилятор не даст забыть перевод при добавлении языка.
internal sealed partial class Strings
{
    public required string TrayAutostart { get; init; }
    public required string TrayOpenFolder { get; init; }
    public required string TrayOpenLog { get; init; }
    public required string TrayExit { get; init; }
    public required string TrayExtensions { get; init; }
    public required string TrayOpenExtensions { get; init; }
    public required string TrayReloadExtensions { get; init; }
    public required string NoExtensions { get; init; }
    public required Func<int, string> ExtensionsReloaded { get; init; }

    public required string AlreadyRunning { get; init; }
    public required string EngineLoadFailed { get; init; }
    public required string ElevatedTitle { get; init; }
    public required string ElevatedText { get; init; }
    public required string RunningTitle { get; init; }
    public required string RunningText { get; init; }
    public required string HeicMissingText { get; init; }
    public required string AutostartFailed { get; init; }
    public required string ErrorTitle { get; init; }
    public required string ConvertFailedTitle { get; init; }
    public required string OriginalDragged { get; init; }
    public required string GrabFailed { get; init; }
    public required string SavedTitle { get; init; }
    public required Func<string, string> SavedIn { get; init; }

    public required string PickOne { get; init; }
    public required string EmptySlot { get; init; }
    public required string SizeUnknown { get; init; }
    public required Func<string, string> TooSmall { get; init; }
    public required Func<int, string> FileCount { get; init; }

    public required string Converting { get; init; }
    public required string Done { get; init; }
    public required string Failed { get; init; }

    // слот размеров (встроенный, для картинок)
    public required string SizeSlot { get; init; }
    public required string Original { get; init; }
    public required string Unchanged { get; init; }
    public required string LongSide { get; init; }
    public required string EachFile { get; init; }
    public required string OwnRatio { get; init; }
    public required string MixedRatios { get; init; }
    public required Func<string, string> RatioOf { get; init; }
    public required Func<int, string> Megapixels { get; init; }
    public required string SizeLarge { get; init; }
    public required string SizeMedium { get; init; }
    public required string SizeAvatar { get; init; }
    public required string SizeIcon { get; init; }
    public required string SizeEmail { get; init; }
    public required string SizePreview { get; init; }
    public required string SizeWeb { get; init; }
    public required string SizePrint { get; init; }

    public required string HintJpg { get; init; }
    public required string HintPng { get; init; }
    public required string HintHeic { get; init; }
    public required string HintWebp { get; init; }
    public required string HintAvif { get; init; }
    public required string HintJxl { get; init; }
    public required string HintTiff { get; init; }
    public required string HintGif { get; init; }
    public required string HintBmp { get; init; }
    public required string HintIco { get; init; }
}

internal static class Loc
{
    // Язык из системы. FORMATWHEEL_LANG=en|ru — проверить перевод, не меняя язык винды.
    public static string Code { get; } =
        (Environment.GetEnvironmentVariable("FORMATWHEEL_LANG") ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName).ToLowerInvariant();

    public static Strings S { get; } = Code switch
    {
        "ru" => Strings.Ru,
        _ => Strings.En // новый язык: файл Strings.xx.cs и строчка здесь
    };
}
