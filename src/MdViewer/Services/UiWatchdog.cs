using System.Diagnostics;
using System.Windows.Threading;

namespace MdViewer.Services;

/// <summary>
/// Следит, отвечает ли окно: раз в секунду ставит пустую задачу с приоритетом ввода.
/// Если окно не отвечает дольше 3 секунд, это записывается в журнал (с причиной, если известна).
/// </summary>
public static class UiWatchdog
{
    private static string _phase = "запуск";

    /// <summary>Что сейчас делает окно — попадает в журнал при зависании.</summary>
    public static void Phase(string phase) => _phase = phase;

    public static void Start(Dispatcher dispatcher)
    {
        var thread = new Thread(() =>
        {
            bool stalled = false;
            var since = Stopwatch.StartNew();
            while (!dispatcher.HasShutdownStarted)
            {
                var op = dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                var sw = Stopwatch.StartNew();
                while (op.Status != DispatcherOperationStatus.Completed && op.Status != DispatcherOperationStatus.Aborted && !dispatcher.HasShutdownStarted)
                {
                    Thread.Sleep(100);
                    if (!stalled && sw.ElapsedMilliseconds > 3000)
                    {
                        stalled = true;
                        since.Restart();
                        Log.Error($"Окно не отвечает больше 3 с (последнее действие: {_phase}), процессор: {Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds:F1} с");
                    }
                }

                if (stalled)
                {
                    stalled = false;
                    Log.Info($"Окно снова отвечает (зависание {since.Elapsed.TotalSeconds + 3:F0} с, последнее действие: {_phase})");
                }

                Thread.Sleep(1000);
            }
        })
        {
            IsBackground = true,
            Name = "UI watchdog",
        };
        thread.Start();
    }
}
