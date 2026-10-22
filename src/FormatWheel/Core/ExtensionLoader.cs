using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using FormatWheel.Sdk;

namespace FormatWheel;

// Одно расширение (класс): конвертер, действия или и то и другое сразу
internal sealed record ExtensionInfo(string Dll, string Name, IConverter? Converter, IWheelActions? Actions, string? Problem)
{
    public bool Enabled => Converter != null || Actions != null;

    public static ExtensionInfo From(string dll, object plugin) =>
        new(dll, (plugin as IConverter)?.Name ?? ((IWheelActions)plugin).Name, plugin as IConverter, plugin as IWheelActions, null);
}

// Грузит расширения-DLL. Встроенные (рядом с exe) доверенные. Пользовательские (в AppData)
// запускаются только после подтверждения: это чужой код в нашем процессе.
internal static class ExtensionLoader
{
    // AssemblyLoadContext не выгружается, поэтому при «Перезагрузить расширения» уже загруженную DLL
    // (тот же путь, тот же хэш) не грузим заново, а отдаём её конвертеры и действия
    private static readonly Dictionary<string, (string Hash, List<object> Plugins)> Loaded = new(StringComparer.OrdinalIgnoreCase);

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
        string hash = Sha256File(dll);
        if (!trusted)
        {
            if (!TrustStore.IsTrusted(dll, hash))
            {
                if (!askTrust(Path.GetFileNameWithoutExtension(dll), dll))
                {
                    yield return new(dll, Path.GetFileNameWithoutExtension(dll), null, null, "not allowed");
                    yield break;
                }
                TrustStore.Trust(dll, hash);
            }
        }

        string fullPath = Path.GetFullPath(dll);
        if (Loaded.TryGetValue(fullPath, out var cached) && cached.Hash == hash)
        {
            foreach (var p in cached.Plugins)
                yield return ExtensionInfo.From(dll, p);
            yield break;
        }

        List<object> plugins = new();
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
            var assembly = context.LoadFromAssemblyPath(fullPath);

            // класс может быть конвертером, действиями или и тем и другим — создаём один экземпляр
            foreach (var type in assembly.GetTypes().Where(t => !t.IsAbstract && !t.IsInterface
                         && (typeof(IConverter).IsAssignableFrom(t) || typeof(IWheelActions).IsAssignableFrom(t))))
            {
                if (Activator.CreateInstance(type) is { } plugin)
                    plugins.Add(plugin);
            }
            if (plugins.Count == 0) error = "no IConverter or IWheelActions found";
            else Loaded[fullPath] = (hash, plugins);
        }
        catch (Exception ex)
        {
            error = (ex is ReflectionTypeLoadException rtle ? rtle.LoaderExceptions.FirstOrDefault()?.Message : null) ?? ex.Message;
        }

        if (error != null)
            yield return new(dll, Path.GetFileNameWithoutExtension(dll), null, null, error);
        else
            foreach (var p in plugins)
                yield return ExtensionInfo.From(dll, p);
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
