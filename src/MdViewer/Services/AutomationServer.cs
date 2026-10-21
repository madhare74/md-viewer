using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MdViewer.Services;

/// <summary>
/// Канал управления для UI-тестов. Включается только переменной окружения MDVIEWER_AUTOMATION_PIPE.
/// Протокол: по строке JSON-запроса — строка JSON-ответа.
/// </summary>
public sealed class AutomationServer
{
    private readonly string _pipeName;
    private readonly MainWindow _window;
    private readonly CancellationTokenSource _cts = new();

    public AutomationServer(string pipeName, MainWindow window)
    {
        _pipeName = pipeName;
        _window = window;
    }

    public void Start()
    {
        var token = _cts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, new UTF8Encoding(false), false, 65536, leaveOpen: true);
                    using var writer = new StreamWriter(server, new UTF8Encoding(false), 65536, leaveOpen: true) { AutoFlush = true };
                    string? line;
                    while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) is not null)
                    {
                        string response;
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var req = doc.RootElement.Clone();
                            response = await _window.Dispatcher.InvokeAsync(() => _window.HandleAutomationAsync(req)).Task.Unwrap().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            response = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
                        }

                        await writer.WriteLineAsync(response).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    // клиент отключился
                }
                catch (Exception ex)
                {
                    Log.Error("Ошибка канала автотестов", ex);
                }
            }
        });
    }

    public void Stop() => _cts.Cancel();
}
