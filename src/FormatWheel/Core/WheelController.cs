using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FormatWheel.Sdk;

namespace FormatWheel;

// Как это работает:
// 1. Человек тащит файл (драг принадлежит Проводнику) и жмёт Shift.
// 2. Под курсором невидимое окно, Проводник сам сообщает путь через DragEnter. Берём конвертер под эти файлы.
// 3. Пока зажат Shift, добавляя/убирая Ctrl и Alt, человек переключается между слотами колеса вживую.
//    Каждый слот пишет в свою ось рецепта, поэтому выборы с разных слотов складываются.
// 4. Отпустил Shift на лепестке: шлём Esc (драг Проводника отменяется), ставим под курсор ловушку,
//    имитируем отпустить+нажать мышь, запускаем свой DoDragDrop с «обещанием» файла.
// 5. Во время своего драга колесо можно открыть снова — выбор дополняется, конвертация перезапускается.
internal sealed class WheelController
{
    private enum State { Idle, Armed, Visible, Handoff, AwaitGrab, OwnDrag, OwnDragWheel }

    private readonly WheelWindow _wheel = new();
    private readonly DragGhostWindow _ghost = new();
    private readonly GrabTrapWindow _trap = new();
    private readonly Action<string, string> _notify;
    private readonly DispatcherTimer _watch;

    private State _state = State.Idle;
    private WheelSlot _slot = WheelSlot.Primary;
    private bool _ctrl, _alt;
    private IConverter? _converter;
    private string[]? _files;       // над колесом во время драга Проводника
    private string[]? _dragFiles;   // файлы нашего драга
    private Recipe _recipe = Recipe.Empty;
    private IReadOnlyList<(string Axis, string Label)> _labels = Array.Empty<(string, string)>(); // подписи выбранного — для карточки
    private BitmapSource? _thumbnail;
    private bool _leaveSignal;
    private DataObject? _pendingData;
    private Task<string[]>? _conversion;
    private CancellationTokenSource? _conversionCts;

    public WheelController(Action<string, string> notify)
    {
        _notify = notify;
        _wheel.FilesEntered += OnFilesEntered;
        _wheel.DragLeftWindow += () => _leaveSignal = true;
        _wheel.DroppedOnWheel += () => { if (_state is State.Armed or State.Visible) Cancel("dropped onto the wheel"); };
        _trap.Grabbed += OnGrabbed;

        _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _watch.Tick += (_, _) =>
        {
            if (_state is State.Armed or State.Visible && !Native.PrimaryButtonDown())
                Cancel("mouse released before Shift");
        };
    }

    // ---------- клавиши ----------

    public void OnShift(bool down)
    {
        if (down) OnShiftDown();
        else Safe(OnShiftUpAsync(), "shift up");
    }

    public void OnCtrl(bool down) { _ctrl = down; OnModifier(); }
    public void OnAlt(bool down) { _alt = down; OnModifier(); }

    private WheelSlot CurrentSlot() =>
        _ctrl && _alt ? WheelSlot.CtrlAlt : _ctrl ? WheelSlot.Ctrl : _alt ? WheelSlot.Alt : WheelSlot.Primary;

    // Модификатор нажали/отпустили при открытом колесе — перескакиваем на другой слот, но не сразу.
    // Ctrl+Alt+Shift физически невозможно отпустить одновременно: Ctrl или Alt отпускаются на пару
    // десятков мс раньше Shift, и колесо успевало переключить меню — выбиралось не то.
    // Поэтому отпускание ждёт SlotReleaseDelay: если за это время отпустят и Shift, берём выбор
    // из меню, которое человек видел. Нажатие ждёт совсем чуть-чуть, чтобы Ctrl+Alt, нажатые
    // не идеально вместе, не мелькали промежуточным меню Ctrl.
    private static readonly TimeSpan SlotPressDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan SlotReleaseDelay = TimeSpan.FromMilliseconds(200);
    private DispatcherTimer? _slotDelay;
    private WheelSlot _pendingSlot;

    private void OnModifier()
    {
        UpdateKeys();
        var slot = CurrentSlot();
        if (_state is not (State.Visible or State.OwnDragWheel))
        {
            _slot = slot; // колесо ещё не открыто — просто запомним, с чего начать
            return;
        }

        _slotDelay?.Stop();
        if (slot == _slot) return; // отпустили и тут же нажали обратно — ничего не меняем

        if (_slotDelay == null)
        {
            _slotDelay = new DispatcherTimer();
            _slotDelay.Tick += (_, _) =>
            {
                _slotDelay.Stop();
                if (_state is State.Visible or State.OwnDragWheel && _pendingSlot != _slot)
                    Safe(ShowSlotAsync(_pendingSlot), "switch slot");
            };
        }
        bool releasing = Mods(slot) < Mods(_slot);
        _pendingSlot = slot;
        _slotDelay.Interval = releasing ? SlotReleaseDelay : SlotPressDelay;
        _slotDelay.Start();
    }

    private static int Mods(WheelSlot slot) => slot switch { WheelSlot.CtrlAlt => 2, WheelSlot.Primary => 0, _ => 1 };

    // отложенное переключение отменяем: выбор — из того меню, что сейчас на экране
    private void DropPendingSlot() => _slotDelay?.Stop();

    // подписи клавиш на колесе: какие модификаторы ведут к меню и какие сейчас зажаты
    private (bool Ctrl, bool Alt) _keysAvailable;

    private void UpdateKeys() => _wheel.SetKeys(_keysAvailable.Ctrl, _keysAvailable.Alt, _ctrl, _alt);

    private void OnShiftDown()
    {
        if (_state == State.OwnDrag)
        {
            Safe(OpenWheelDuringOwnDragAsync(), "wheel during own drag");
            return;
        }
        if (_state != State.Idle || !Native.PrimaryButtonDown()) return;

        _slot = CurrentSlot();
        _files = null;
        _converter = null;
        _thumbnail = null;
        _recipe = Recipe.Empty;
        _labels = Array.Empty<(string, string)>();
        Native.GetCursorPos(out var cursor);
        _wheel.ShowAt(cursor, acceptDrops: true);
        _state = State.Armed;
        _watch.Start();
        Log.Write($"Armed ({_slot}) at ({cursor.X},{cursor.Y})");
    }

    private void OnFilesEntered(string[] files)
    {
        if (_state is not (State.Armed or State.Visible)) return;
        if (_files != null && files.SequenceEqual(_files)) return;

        var converter = Converters.For(files);
        if (converter == null)
        {
            // никакой конвертер не берёт эти файлы (папка, неизвестный тип) — колеса нет, драг как обычно
            Cancel("no converter for: " + string.Join(" | ", files.Select(Path.GetFileName)));
            return;
        }

        _converter = converter;
        _files = files;
        Log.Write($"Files over wheel ({converter.Name}): " + string.Join(" | ", files));
        Safe(LoadThumbnailAsync(files[0]), "thumbnail");
        Safe(RevealAsync(files), "reveal");
    }

    private async Task RevealAsync(string[] files)
    {
        await ShowSlotAsync(_slot);
        if (_state == State.Armed) _state = State.Visible;
        _wheel.SetThumbnail(_thumbnail);
    }

    private async Task OpenWheelDuringOwnDragAsync()
    {
        if (_dragFiles == null || _converter == null) return;
        _state = State.OwnDragWheel; // сразу, чтобы второе нажатие не открыло второе колесо
        _slot = CurrentSlot();
        Native.GetCursorPos(out var cursor);
        _wheel.ShowAt(cursor, acceptDrops: false);
        await ShowSlotAsync(_slot);
        _wheel.SetThumbnail(_thumbnail);
        _ghost.SetDimmed(true);
    }

    // строит меню выбранного слота и проявляет колесо
    private async Task ShowSlotAsync(WheelSlot slot)
    {
        _slot = slot;
        var files = _state == State.OwnDragWheel ? _dragFiles : _files;
        if (_converter == null || files == null) return;

        if (_menus == null || _menus.Files != files || _menus.Converter != _converter)
            _menus = new MenuCache(_converter, files, new());
        var cache = _menus;

        // MenuFor может читать файлы (размер картинки), поэтому и меню, и число слотов — не в UI-потоке
        var (options, keys) = await Task.Run(() => (Menu(cache, slot), AvailableKeys(cache)));
        if ((_state == State.OwnDragWheel ? _dragFiles : _files) != files) return; // пока готовили, файлы сменились
        if (_slot != slot) return; // пока готовили, переключились на другой слот

        _slotOptions = options;
        _keysAvailable = keys;
        UpdateKeys();
        _wheel.SetItems(options.Select(o => new WheelItem(o.Label, o.Hint, IconConvert.ToSource(o.Icon), IsCurrent(o))).ToList());
        _wheel.Reveal(Title(files), options.Count == 0 ? Loc.S.EmptySlot : null);
    }

    private IReadOnlyList<WheelOption> _slotOptions = Array.Empty<WheelOption>();

    // меню слотов на текущие файлы считаем один раз за драг, а не на каждое переключение Ctrl/Alt
    private sealed record MenuCache(IConverter Converter, string[] Files, ConcurrentDictionary<WheelSlot, IReadOnlyList<WheelOption>> Menus);
    private MenuCache? _menus;

    private static IReadOnlyList<WheelOption> Menu(MenuCache cache, WheelSlot slot) =>
        cache.Menus.GetOrAdd(slot, s => cache.Converter.MenuFor(s, cache.Files).ToList());

    // к каким меню ведут Ctrl и Alt — их подписи показываем на колесе
    private static (bool Ctrl, bool Alt) AvailableKeys(MenuCache cache)
    {
        bool ctrlAlt = Menu(cache, WheelSlot.CtrlAlt).Count > 0;
        return (ctrlAlt || Menu(cache, WheelSlot.Ctrl).Count > 0, ctrlAlt || Menu(cache, WheelSlot.Alt).Count > 0);
    }

    // выбор лепестка: новый рецепт и подписи для карточки
    private (Recipe Recipe, IReadOnlyList<(string Axis, string Label)> Labels) Choose(WheelOption option)
    {
        var labels = _labels.Where(l => l.Axis != option.Axis).ToList();
        if (!option.IsReset) labels.Add((option.Axis, option.Label));
        return (_recipe.With(option), labels);
    }

    private bool IsCurrent(WheelOption o) =>
        o.IsReset ? !_recipe.Has(o.Axis) : Equals(_recipe.GetRaw(o.Axis), o.Value);

    private async Task LoadThumbnailAsync(string path)
    {
        var thumbnail = await Thumbnails.GetAsync(path, 256);
        if (_files == null || _files[0] != path) return;
        _thumbnail = thumbnail;
        if (_state is State.Visible or State.OwnDragWheel) _wheel.SetThumbnail(thumbnail);
    }

    // ---------- выбор / пересадка ----------

    private async Task OnShiftUpAsync()
    {
        DropPendingSlot();
        try
        {
            switch (_state)
            {
                case State.Armed: Cancel("Shift released before the wheel opened"); return;
                case State.OwnDragWheel: ApplyDuringOwnDrag(); return;
                case State.Visible: break;
                default: return;
            }

            int index = _wheel.SelectedIndex;
            if (index < 0 || index >= _slotOptions.Count || _files == null || _converter == null || !Native.PrimaryButtonDown())
            {
                Cancel($"Shift released with no choice (petal {index})");
                return;
            }

            var (recipe, labels) = Choose(_slotOptions[index]);
            await HandoffAsync(_files, recipe, labels);
        }
        catch (Exception ex)
        {
            Log.Write("Error: " + ex);
            _notify(Loc.S.ErrorTitle, ex.Message);
            Cancel("exception");
        }
    }

    private void ApplyDuringOwnDrag()
    {
        int index = _wheel.SelectedIndex;
        _wheel.CloseAnimated(inputTransparentNow: true);
        _ghost.SetDimmed(false);
        _state = State.OwnDrag;
        if (index < 0 || index >= _slotOptions.Count || _dragFiles == null) return;

        var (updated, labels) = Choose(_slotOptions[index]);
        if (updated.Equals(_recipe)) return;
        Log.Write("Recipe changed mid-drag");
        StartConversion(_dragFiles, updated, labels, restart: true);
    }

    private async Task HandoffAsync(string[] files, Recipe recipe, IReadOnlyList<(string Axis, string Label)> labels)
    {
        _state = State.Handoff;
        _watch.Stop();
        _dragFiles = files;
        Log.Write("Handoff starting");

        _wheel.CloseAnimated(inputTransparentNow: false); // ещё ловит DragLeave — по нему узнаем об отмене Проводника
        StartConversion(files, recipe, labels, restart: false);

        _leaveSignal = false;
        Native.SendEscape();
        var wait = Stopwatch.StartNew();
        while (!_leaveSignal && wait.ElapsedMilliseconds < 250) await Task.Delay(10);
        await Task.Delay(40); // пусть цикл Проводника точно закончится
        Log.Write(_leaveSignal ? $"Explorer drag cancelled in {wait.ElapsedMilliseconds} ms" : "No cancel signal, going on by timeout");

        if (!Native.PrimaryButtonDown()) { Cancel("mouse released during handoff"); return; }

        _wheel.MakeInputTransparent();
        Native.GetCursorPos(out var cursor);
        bool slow = _converter!.IsSlow;
        // для долгих конвертаций файл «появляется» у получателя только когда готов (кольцо позеленело)
        _pendingData = new DataObject(new LazyFileDataObject(WaitForResult, () => !slow || _conversion?.IsCompleted == true));
        _trap.ArmAt(cursor);
        await Task.Delay(20);
        _state = State.AwaitGrab;
        Native.SendPrimaryUpDown();

        await Task.Delay(600);
        if (_state == State.AwaitGrab)
        {
            Cancel("fake click never reached the trap");
            _notify("Format Wheel", Loc.S.GrabFailed);
        }
    }

    private void StartConversion(string[] files, Recipe recipe, IReadOnlyList<(string Axis, string Label)> labels, bool restart)
    {
        var converter = _converter!;
        _recipe = recipe;
        _labels = labels;
        // короткая подпись на карточке: что именно сейчас выбрано
        string badge = labels.Count > 0 ? string.Join(" · ", labels.Select(l => l.Label)) : converter.Name;
        int session = restart ? _ghost.Retarget(badge) : _ghost.Begin(_thumbnail, badge, files.Length);

        CancelConversion(); // передумали посреди драга — старую глушим (для видео это минуты процессора)
        var cts = new CancellationTokenSource();
        _conversionCts = cts;

        var progress = new Progress<double>(p => _ghost.SetProgress(session, p));
        var sw = Stopwatch.StartNew();
        var conversion = Task.Run(() =>
        {
            var result = ConversionRunner.ConvertAll(converter, files, recipe, progress, cts.Token);
            Log.Write($"Converted in {sw.ElapsedMilliseconds} ms");
            return result;
        }, cts.Token);
        _ = conversion.ContinueWith(t =>
        {
            if (_conversionCts == cts) _conversionCts = null;
            cts.Dispose();
            _ghost.SetDone(session, t.Status == TaskStatus.RanToCompletion);
        }, TaskScheduler.FromCurrentSynchronizationContext());
        _conversion = conversion;
    }

    private void OnGrabbed()
    {
        if (_state != State.AwaitGrab || _pendingData == null) return;

        var data = _pendingData;
        _pendingData = null;
        _state = State.OwnDrag;
        _trap.Disarm(); // мышь захватит служебное окно OLE в нашем потоке

        bool slow = _converter?.IsSlow == true;
        var originals = _dragFiles ?? Array.Empty<string>();
        bool escaped = false;
        Native.POINT dropPoint = default;
        QueryContinueDragEventHandler follow = (_, e) =>
        {
            _ghost.MoveToCursor();
            if (e.EscapePressed) escaped = true;
            Native.GetCursorPos(out dropPoint);
        };
        _trap.QueryContinueDrag += follow;
        try
        {
            var effect = DragDrop.DoDragDrop(_trap, data, DragDropEffects.Copy);
            Log.Write("Own drag finished: " + effect);
            if (effect == DragDropEffects.None)
            {
                // Долгую конвертацию бросили раньше, чем файл был готов (или получатель запомнил «файлов нет»
                // ещё при входе курсора). Не выкидываем работу: доделаем и положим файл сами.
                if (slow && !escaped && _conversion is { } pending)
                    DeliverLater(pending, ShellTarget.FolderAt(dropPoint), originals);
                else
                    CancelConversion(); // никуда не бросили — незачем доделывать
            }
        }
        catch (Exception ex)
        {
            Log.Write("DoDragDrop error: " + ex);
            _notify(Loc.S.ErrorTitle, ex.Message);
        }
        finally
        {
            _trap.QueryContinueDrag -= follow;
            if (_state == State.OwnDragWheel) _wheel.CloseAnimated(inputTransparentNow: true);
            _state = State.Idle;
            _files = null;
            _dragFiles = null;
            _converter = null;
        }
        _ghost.FadeOutFollowing();
    }

    // Получатели дёргают это сотнями раз при наведении и ещё раз при броске.
    // Вложенный цикл сообщений тут ловил дедлок — просто ждём.
    private string[] WaitForResult()
    {
        var conversion = _conversion;
        var originals = _dragFiles ?? Array.Empty<string>();
        if (conversion == null) return originals;

        if (!conversion.IsCompleted)
            ((IAsyncResult)conversion).AsyncWaitHandle.WaitOne();

        try { return conversion.GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Exception inner = ex is AggregateException agg ? agg.Flatten().InnerException ?? ex : ex;
            if (inner is OperationCanceledException) return originals;
            Log.Write("Conversion failed: " + inner);
            _wheel.Dispatcher.InvokeAsync(() => _notify(Loc.S.ConvertFailedTitle, inner.Message + "\n" + Loc.S.OriginalDragged));
            return originals;
        }
    }

    // folder — папка под курсором при броске; null — кладём рядом с оригиналом
    private async void DeliverLater(Task<string[]> conversion, string? folder, string[] originals)
    {
        // конвертация теперь сама по себе: следующий драг не должен её заглушить
        _conversionCts = null;
        _conversion = null;
        Log.Write("Dropped before the result was ready, will save to " + (folder ?? "the source folder"));
        try
        {
            string[] results = await conversion;
            string[] saved = await Task.Run(() => results.Select((file, i) =>
                SaveCopy(file, folder ?? Path.GetDirectoryName(originals[Math.Min(i, originals.Length - 1)])!)).ToArray());
            Log.Write("Saved: " + string.Join(" | ", saved));
            _notify(Loc.S.SavedTitle, Loc.S.SavedIn(saved.Length == 1 ? saved[0] : Path.GetDirectoryName(saved[0])!));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("Conversion failed: " + ex);
            _notify(Loc.S.ConvertFailedTitle, ex.Message);
        }
    }

    // не перетираем существующий файл: «видео (2).mp4»
    private static string SaveCopy(string file, string folder)
    {
        string name = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file);
        string target = Path.Combine(folder, name + ext);
        for (int n = 2; File.Exists(target); n++) target = Path.Combine(folder, $"{name} ({n}){ext}");
        File.Copy(file, target);
        return target;
    }

    private void CancelConversion()
    {
        var cts = _conversionCts;
        _conversionCts = null;
        if (cts == null) return;
        try { cts.Cancel(); } catch { }
    }

    private void Cancel(string reason)
    {
        Log.Write("Cancel: " + reason);
        _watch.Stop();
        _wheel.CloseAnimated(inputTransparentNow: true);
        _trap.Disarm();
        if (_state is State.Handoff or State.AwaitGrab) { _ghost.HideNow(); CancelConversion(); }
        _state = State.Idle;
        _files = null;
        _dragFiles = null;
        _pendingData = null;
    }

    private void Safe(Task task, string what) =>
        task.ContinueWith(t => Log.Write($"Error ({what}): {t.Exception?.Flatten().InnerException}"), TaskContinuationOptions.OnlyOnFaulted);

    private string Title(string[] files) => files.Length == 1 ? Path.GetFileName(files[0]) : Loc.S.FileCount(files.Length);
}
