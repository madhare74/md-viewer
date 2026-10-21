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

    public static bool SendToPrimary(IReadOnlyList<string> files)
    {
        try
        {
            NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
            using var client = new NamedPipeClientStream(".", BaseName + "-pipe", PipeDirection.Out);
            client.Connect(3000);
            byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(files));
            client.Write(data, 0, data.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось передать файлы запущенному приложению", ex);
            return false;
        }
    }

    public void StartListening(Action<string[]> onFiles)
    {
        var token = _cts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(BaseName + "-pipe", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var ms = new MemoryStream();
                    await server.CopyToAsync(ms, token).ConfigureAwait(false);
                    var files = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(ms.ToArray())) ?? Array.Empty<string>();
                    onFiles(files);
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
