using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MdViewer.Services;

/// <summary>
/// Одно окно на пользователя: если приложение уже запущено, новый процесс передаёт ему файлы
/// по именованному каналу и завершается — файл открывается вкладкой в том же окне.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static string BaseName
    {
        get
        {
            string id = Environment.GetEnvironmentVariable("MDVIEWER_INSTANCE_ID") ?? Environment.UserName;
            var sb = new StringBuilder("MDViewer-");
            foreach (char c in id)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }

            return sb.ToString();
        }
    }

    public static SingleInstance? TryAcquire()
    {
        var m = new Mutex(true, @"Local\" + BaseName + "-mutex", out bool created);
        if (created)
        {
            return new SingleInstance(m);
        }

        m.Dispose();
        return null;
    }

    /// <summary>
    /// Передаёт файлы запущенному окну. true — только если окно действительно их приняло:
    /// зависший экземпляр не ответит, и тогда откроется новое окно.
    /// </summary>
    public static bool SendToPrimary(IReadOnlyList<string> files)
    {
        try
        {
            NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
            using var client = new NamedPipeClientStream(".", BaseName + "-pipe", PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(3000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            writer.WriteLine(JsonSerializer.Serialize(files));
            var ack = reader.ReadLineAsync();
            if (!ack.Wait(TimeSpan.FromSeconds(6)) || ack.Result != "ok")
            {
                Log.Error("Запущенное окно MD Viewer не ответило — открываю новое окно");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось передать файлы запущенному приложению", ex);
            return false;
        }
    }

    /// <summary>Принимает файлы от новых запусков. onFiles должен вернуть true, когда окно открыло их.</summary>
    public void StartListening(Func<string[], Task<bool>> onFiles)
    {
        var token = _cts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(BaseName + "-pipe", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                    using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                    string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                    var files = JsonSerializer.Deserialize<string[]>(line ?? "[]") ?? Array.Empty<string>();
                    var handled = onFiles(files);
                    bool ok = await Task.WhenAny(handled, Task.Delay(4000, token)).ConfigureAwait(false) == handled && handled.Result;
                    await writer.WriteLineAsync(ok ? "ok" : "busy").ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error("Ошибка канала одного экземпляра", ex);
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
            // ignore
        }

        _mutex.Dispose();
    }
}
