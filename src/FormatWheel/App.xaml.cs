using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using FormatWheel.Sdk;

namespace FormatWheel;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private KeyboardHook? _hook;
    private WheelController? _controller;
    private System.Windows.Forms.NotifyIcon? _tray;
    private List<ExtensionInfo> _extensions = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // что бы ни случилось — курсор не должен остаться запертым в колесе
        DispatcherUnhandledException += (_, args) => { Native.ReleaseCursor(); Log.Write("Unhandled: " + args.Exception); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Native.ReleaseCursor();

        _singleInstance = new Mutex(true, @"Local\FormatWheel.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show(Loc.S.AlreadyRunning, "Format Wheel");
            Shutdown();
            return;
        }

        OutputPath.TempRoot = Paths.TempRoot; // расширения пишут файлы сюда же
        StartCleanup();
        Log.TrimOnStartup();
        Log.Write($"=== Format Wheel started, UI language: {System.Globalization.CultureInfo.CurrentUICulture.Name} ===");
        CreateTray();

        try { Formats.Initialize(); }
        catch (Exception ex)
        {
            Log.Write("Formats init failed: " + ex);
            MessageBox.Show(Loc.S.EngineLoadFailed + "\n" + ex.Message, "Format Wheel");
            Shutdown();
            return;
        }

        LoadConverters();

        Perf.Apply();
        // Magick.NET сам распараллеливает каждую картинку на все ядра, а мы ещё и несколько файлов
        // параллельно — на слабом ПК это забивало процессор целиком. Делим ядра честно.
        ImageMagick.ResourceLimits.Thread = (ulong)Math.Max(1, Environment.ProcessorCount / 2);

        _controller = new WheelController(Notify);
        _hook = new KeyboardHook();
        _hook.ShiftChanged += down => Dispatcher.InvokeAsync(() => _controller.OnShift(down));
        _hook.CtrlChanged += down => Dispatcher.InvokeAsync(() => _controller.OnCtrl(down));
        _hook.AltChanged += down => Dispatcher.InvokeAsync(() => _controller.OnAlt(down));

        try { _hook.Start(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Format Wheel"); Shutdown(); return; }

        _tips = new DragTips(() => _controller.IsIdle, ShowTip);

        if (IsElevated()) Notify(Loc.S.ElevatedTitle, Loc.S.ElevatedText);
        else if (Formats.HeicHidden) Notify(Loc.S.RunningTitle, Loc.S.HeicMissingText);
        else Notify(Loc.S.RunningTitle, Loc.S.RunningText);
    }

    // Чистка папки копий: сразу при запуске и потом раз в час — программа живёт в трее неделями,
    // и чистка только при старте копила гигабайты видео. В фоне: удаление гигабайт не должно тормозить запуск.
    private static readonly TimeSpan CopiesMaxAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan CopiesMinAge = TimeSpan.FromMinutes(10);
    private const long CopiesMaxBytes = 2L * 1024 * 1024 * 1024; // 2 ГБ
    private Timer? _cleanup;

    private void StartCleanup() =>
        _cleanup = new Timer(_ => Paths.Cleanup(CopiesMaxAge, CopiesMaxBytes, CopiesMinAge), null, TimeSpan.FromSeconds(5), TimeSpan.FromHours(1));

    // картинки — встроенный конвертер, всё остальное из DLL-расширений
    private void LoadConverters()
    {
        _extensions = ExtensionLoader.LoadAll(AskTrust);
        Converters.Set(new IConverter[] { new ImageConverter() }.Concat(_extensions.Where(x => x.Enabled).Select(x => x.Converter!)));
    }

    private static bool AskTrust(string name, string dll) =>
        MessageBox.Show($"{name}\n{dll}\n\nEnable this extension? Only allow ones you trust.",
            Loc.S.TrayExtensions, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void CreateTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();

        var autostart = new System.Windows.Forms.ToolStripMenuItem(Loc.S.TrayAutostart) { CheckOnClick = true, Checked = Autostart.IsEnabled };
        autostart.CheckedChanged += (_, _) =>
        {
            try { Autostart.Set(autostart.Checked); }
            catch (Exception ex) { Notify(Loc.S.AutostartFailed, ex.Message); }
        };
        menu.Opening += (_, _) => autostart.Checked = Autostart.IsEnabled;
        menu.Items.Add(autostart);

        var extensions = new System.Windows.Forms.ToolStripMenuItem(Loc.S.TrayExtensions);
        menu.Opening += (_, _) => FillExtensionsMenu(extensions);
        menu.Items.Add(extensions);

        menu.Items.Add(Loc.S.TrayOpenFolder, null, (_, _) => { Directory.CreateDirectory(Paths.TempRoot); Process.Start("explorer.exe", Paths.TempRoot); });
        menu.Items.Add(Loc.S.TrayOpenLog, null, (_, _) => Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Loc.S.TrayExit, null, (_, _) => Shutdown());

        _tray = new System.Windows.Forms.NotifyIcon { Icon = LoadAppIcon(), Text = "Format Wheel", Visible = true, ContextMenuStrip = menu };
        _tray.BalloonTipClicked += (_, _) => OnBalloonClicked();
        _tray.BalloonTipClosed += (_, _) => _tipOnScreen = false;
    }

    private void FillExtensionsMenu(System.Windows.Forms.ToolStripMenuItem root)
    {
        foreach (var old in root.DropDownItems.Cast<System.Windows.Forms.ToolStripItem>().ToArray()) old.Dispose();
        root.DropDownItems.Clear();
        if (_extensions.Count == 0)
            root.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem(Loc.S.NoExtensions) { Enabled = false });

        foreach (var ext in _extensions)
        {
            var item = new System.Windows.Forms.ToolStripMenuItem((ext.Enabled ? "✓  " : "✗  ") + ext.Name) { ToolTipText = ext.Problem ?? ext.Dll };
            if (!ext.Enabled) item.Click += (_, _) => Notify(ext.Name, ext.Problem ?? "");
            root.DropDownItems.Add(item);
        }

        root.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        // папка рядом с программой (%LOCALAPPDATA%\Programs\FormatWheel\extensions) — туда ставят установщики расширений
        root.DropDownItems.Add(Loc.S.TrayOpenExtensions, null, (_, _) => { Directory.CreateDirectory(Paths.BundledExtensions); Process.Start("explorer.exe", Paths.BundledExtensions); });
        root.DropDownItems.Add(Loc.S.TrayReloadExtensions, null, (_, _) =>
        {
            LoadConverters();
            Notify("Format Wheel", Loc.S.ExtensionsReloaded(_extensions.Count(x => x.Enabled)));
        });
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try { if (Environment.ProcessPath is { } exe && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } icon) return icon; }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    private void Notify(string title, string text)
    {
        _tipOnScreen = false;
        _tray?.ShowBalloonTip(3000, title, text, System.Windows.Forms.ToolTipIcon.Info);
    }

    // Напоминалка во время драга. У трея одно облачко на всех, поэтому помним, что сейчас на экране именно она:
    // клик по ней выключает напоминалки навсегда, а клик по обычному уведомлению — нет.
    private DragTips? _tips;
    private bool _tipOnScreen;

    private void ShowTip(string title, string text)
    {
        _tray?.ShowBalloonTip(4000, title, text, System.Windows.Forms.ToolTipIcon.None);
        _tipOnScreen = true;
    }

    private void OnBalloonClicked()
    {
        if (!_tipOnScreen) return;
        _tipOnScreen = false;
        _tips?.TurnOff();
        Notify(Loc.S.DragTipsOffTitle, Loc.S.DragTipsOffText);
    }

    // из-под админа драг из обычного Проводника не работает (UIPI), предупреждаем
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Native.ReleaseCursor();
        _hook?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
