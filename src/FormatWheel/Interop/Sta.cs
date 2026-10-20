using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FormatWheel;

// Shell COM и WPF Imaging капризничают вне STA, поэтому для них отдельный поток.
internal static class Sta
{
    public static Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
            // WPF-картинки заводят на потоке свой Dispatcher со скрытым окном. Поток просто так завершится —
            // окно снесёт система, но GCHandle на него не освободится, и Dispatcher останется в памяти навсегда.
            finally { Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); }
        })
        { IsBackground = true, Name = "FormatWheel STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    public static void Run(Action action) =>
        RunAsync<object?>(() => { action(); return null; }).GetAwaiter().GetResult();
}
