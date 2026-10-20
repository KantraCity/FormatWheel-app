using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using FormatWheel.Sdk;

namespace FormatWheel;

internal sealed record ExtensionInfo(string Dll, string Name, IConverter? Converter, string? Problem)
{
    public bool Enabled => Converter != null;
}

// Грузит расширения-DLL. Встроенные (рядом с exe) доверенные. Пользовательские (в AppData)
// запускаются только после подтверждения: это чужой код в нашем процессе.
internal static class ExtensionLoader
{
    // askTrust(имя, dll) — спросить человека, можно ли запускать это расширение
    public static List<ExtensionInfo> LoadAll(Func<string, string, bool> askTrust)
    {
        var result = new List<ExtensionInfo>();
        try { Directory.CreateDirectory(Paths.UserExtensions); } catch { }

        foreach (var (dir, trusted) in new[] { (Paths.BundledExtensions, true), (Paths.UserExtensions, false) })
        {
            if (!Directory.Exists(dir)) continue;
            // каждое расширение в своей подпапке со всеми зависимостями
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                foreach (string dll in Directory.EnumerateFiles(sub, "*.dll").Where(LooksLikeExtension))
                {
                    foreach (var info in LoadFile(dll, trusted, askTrust))
                    {
                        Log.Write($"Extension {Path.GetFileName(dll)}: {(info.Enabled ? "enabled " + info.Name : "disabled — " + info.Problem)}");
                        result.Add(info);
                    }
                }
            }
        }
        return result;
    }

    // имя по соглашению: FormatWheel.*.dll (чтобы не грузить все зависимости подряд)
    private static bool LooksLikeExtension(string dll) =>
        Path.GetFileName(dll).StartsWith("FormatWheel.", StringComparison.OrdinalIgnoreCase)
        && !Path.GetFileName(dll).Equals("FormatWheel.Sdk.dll", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<ExtensionInfo> LoadFile(string dll, bool trusted, Func<string, string, bool> askTrust)
    {
        if (!trusted)
        {
            string hash = Sha256File(dll);
            if (!TrustStore.IsTrusted(dll, hash))
            {
                if (!askTrust(Path.GetFileNameWithoutExtension(dll), dll))
                {
                    yield return new(dll, Path.GetFileNameWithoutExtension(dll), null, "not allowed");
                    yield break;
                }
                TrustStore.Trust(dll, hash);
            }
        }

        List<IConverter> converters = new();
        string? error = null;
        try
        {
            // своя папка расширения в путях поиска — там лежат его зависимости
            var context = new AssemblyLoadContext(Path.GetFileNameWithoutExtension(dll), isCollectible: false);
            context.Resolving += (ctx, name) =>
            {
                string candidate = Path.Combine(Path.GetDirectoryName(dll)!, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dll));

            foreach (var type in assembly.GetTypes().Where(t => typeof(IConverter).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface))
            {
                if (Activator.CreateInstance(type) is IConverter converter)
                    converters.Add(converter);
            }
            if (converters.Count == 0) error = "no IConverter found";
        }
        catch (Exception ex)
        {
            error = (ex is ReflectionTypeLoadException rtle ? rtle.LoaderExceptions.FirstOrDefault()?.Message : null) ?? ex.Message;
        }

        if (error != null)
            yield return new(dll, Path.GetFileNameWithoutExtension(dll), null, error);
        else
            foreach (var c in converters)
                yield return new(dll, c.Name, c, null);
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

// Какие пользовательские расширения уже разрешили. Обновили DLL — хэш другой, спросим снова.
internal static class TrustStore
{
    private static string FilePath => Path.Combine(Paths.AppData, "trusted-extensions.txt");

    public static bool IsTrusted(string dll, string hash) => Load().Contains(Key(dll, hash));

    public static void Trust(string dll, string hash)
    {
        var set = Load();
        set.Add(Key(dll, hash));
        Directory.CreateDirectory(Paths.AppData);
        File.WriteAllLines(FilePath, set.OrderBy(x => x));
    }

    private static HashSet<string> Load()
    {
        try { return File.Exists(FilePath) ? File.ReadAllLines(FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase); }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static string Key(string dll, string hash) => Path.GetFullPath(dll) + "|" + hash;
}
