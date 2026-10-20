using System;
using System.Threading;
using System.Threading.Tasks;

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
        })
        { IsBackground = true, Name = "FormatWheel STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    public static void Run(Action action) =>
        RunAsync<object?>(() => { action(); return null; }).GetAwaiter().GetResult();
}
