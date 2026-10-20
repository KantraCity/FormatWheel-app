using System;
using Microsoft.Win32;

namespace FormatWheel;

// HKCU\...\Run — без админских прав. Установщик пишет то же значение, так что галочка в трее с ним дружит.
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FormatWheel";

    private static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Can't resolve exe path.");

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Trim('"').Equals(ExePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{ExePath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
