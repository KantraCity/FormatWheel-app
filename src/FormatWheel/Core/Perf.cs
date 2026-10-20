using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FormatWheel;

// Лёгкий режим для слабых ПК: без пружинящих анимаций, поворотов и поэтапного появления лепестков,
// анимации 30 кадров/с вместо 60. Прозрачные окна WPF на слабом железе перерисовываются через процессор,
// и каждый лишний кадр там заметен.
// Включается сам, если видеокарта не тянет WPF полностью (Tier < 2) или ядер 4 и меньше.
// FORMATWHEEL_LITE=1 — включить принудительно, FORMATWHEEL_LITE=0 — выключить.
internal static class Perf
{
    public static bool Lite { get; } = Detect();

    private static bool Detect()
    {
        string? forced = Environment.GetEnvironmentVariable("FORMATWHEEL_LITE");
        if (forced is "1" or "true") return true;
        if (forced is "0" or "false") return false;
        int tier = RenderCapability.Tier >> 16;
        return tier < 2 || Environment.ProcessorCount <= 4;
    }

    // вызывать до создания окон
    public static void Apply()
    {
        Log.Write($"Render tier {RenderCapability.Tier >> 16}, {Environment.ProcessorCount} cores, lite mode: {Lite}");
        if (Lite)
            Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = 30 });
    }

    // длительность анимации: в лёгком режиме короче
    public static Duration Ms(int ms) => new(TimeSpan.FromMilliseconds(Lite ? Math.Min(ms, 90) : ms));
}
