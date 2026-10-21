using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using FlaApp = FlaUI.Core.Application;

namespace MdViewer.UiTests;

/// <summary>Запуск приложения для теста: отдельная папка настроек, канал управления, FlaUI.</summary>
public sealed class AppSession : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly string _name;

    private AppSession(string name, string dataDir, string instanceId, FlaApp app, UIA3Automation automation, Window window, NamedPipeClientStream pipe)
    {
        _name = name;
        DataDir = dataDir;
        InstanceId = instanceId;
        App = app;
        Automation = automation;
        Window = window;
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65536, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, leaveOpen: true) { AutoFlush = true };
    }

    public string DataDir { get; }

    public string InstanceId { get; }

    public FlaApp App { get; }

    public UIA3Automation Automation { get; }

    public Window Window { get; }

    public static string ExePath
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("MDVIEWER_EXE");
            if (!string.IsNullOrEmpty(env))
            {
                return env;
            }

            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir is not null; i++)
            {
                string candidate = Path.Combine(dir, "publish", "MDViewer.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'))!;
            }

            throw new FileNotFoundException("Не найден MDViewer.exe — задайте MDVIEWER_EXE");
        }
    }

    public static string OutDir
    {
        get
        {
            string dir = Environment.GetEnvironmentVariable("MDVIEWER_TEST_OUT") ?? Path.Combine(AppContext.BaseDirectory, "ui-test-results");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SamplesDir => Path.Combine(AppContext.BaseDirectory, "samples");

    /// <summary>Рабочая копия образцов (тесты меняют файлы).</summary>
    public static string CopySamples()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mdv-ui-" + Guid.NewGuid().ToString("N")[..8]);
        CopyDir(SamplesDir, dir);
        return dir;
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from))
        {
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        }

        foreach (string d in Directory.GetDirectories(from))
        {
            CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }
    }

    public static AppSession Start(string name, string[]? files = null, double uiScale = 1.0, string theme = "Light", string? dataDir = null, string? instanceId = null, string? settingsJson = null, bool noRestore = true)
    {
        dataDir ??= Path.Combine(Path.GetTempPath(), "mdv-data-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dataDir);
        string settingsPath = Path.Combine(dataDir, "settings.json");
        if (settingsJson is not null)
        {
            File.WriteAllText(settingsPath, settingsJson);
        }
        else if (!File.Exists(settingsPath))
        {
            File.WriteAllText(settingsPath, "{\"Theme\":\"" + theme + "\",\"Window\":{\"Left\":0,\"Top\":0,\"Width\":1280,\"Height\":800,\"Maximized\":false}}");
        }

        instanceId ??= "t" + Guid.NewGuid().ToString("N")[..8];
        string pipeName = "mdv-auto-" + Guid.NewGuid().ToString("N")[..8];
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
        };
        foreach (string f in files ?? Array.Empty<string>())
        {
            psi.ArgumentList.Add(f);
        }

        psi.Environment["MDVIEWER_DATA_DIR"] = dataDir;
        psi.Environment["MDVIEWER_AUTOMATION_PIPE"] = pipeName;
        psi.Environment["MDVIEWER_INSTANCE_ID"] = instanceId;
        if (noRestore)
        {
            psi.Environment["MDVIEWER_NO_RESTORE"] = "1";
        }

        if (uiScale != 1.0)
        {
            psi.Environment["MDVIEWER_UI_SCALE"] = uiScale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var app = FlaApp.Launch(psi);
        var automation = new UIA3Automation();
        var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(60)) ?? throw new InvalidOperationException("Главное окно не появилось");

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                pipe.Connect(2000);
                break;
            }
            catch (TimeoutException) when (DateTime.UtcNow < deadline)
            {
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(300);
            }
        }

        var s = new AppSession(name, dataDir, instanceId, app, automation, window, pipe);
        s.Call(new { cmd = "ping" });
        if (files is { Length: > 0 })
        {
            s.WaitUntil(() => s.State().GetProperty("tabs").GetArrayLength() >= files.Length, 60, "вкладки из командной строки");
            s.WaitRendered();
        }

        return s;
    }

    public JsonElement Call(object request, int timeoutSec = 90)
    {
        string line = JsonSerializer.Serialize(request);
        _writer.WriteLine(line);
        var task = _reader.ReadLineAsync();
        if (!task.Wait(TimeSpan.FromSeconds(timeoutSec)))
        {
            throw new TimeoutException("Нет ответа на " + line);
        }

        using var doc = JsonDocument.Parse(task.Result ?? "{}");
        var root = doc.RootElement.Clone();
        if (!root.GetProperty("ok").GetBoolean())
        {
            throw new InvalidOperationException("Ошибка команды " + line + ": " + root.GetProperty("error").GetString());
        }

        return root.TryGetProperty("data", out var data) ? data.Clone() : default;
    }

    public JsonElement State() => Call(new { cmd = "state" });

    public JsonElement PageState()
    {
        var raw = Call(new { cmd = "eval", js = "JSON.stringify(mdv.getState())" });
        using var doc = JsonDocument.Parse(raw.GetString()!);
        return doc.RootElement.Clone();
    }

    public string Eval(string js)
    {
        var r = Call(new { cmd = "eval", js });
        return r.ValueKind == JsonValueKind.String ? r.GetString()! : r.GetRawText();
    }

    public string PreviewText() => Eval("mdv.getPreviewText()");

    public void WaitRendered() => Call(new { cmd = "waitRendered", timeout = 30000 });

    public void Screenshot(string fileName, string method = "print")
    {
        Call(new { cmd = "screenshot", path = Path.Combine(OutDir, "screenshots", fileName), method, delay = 600 });
    }

    public void WaitUntil(Func<bool> cond, double seconds, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        Exception? last = null;
        while (DateTime.UtcNow < until)
        {
            try
            {
                if (cond())
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException($"Не дождались: {what}" + (last is null ? string.Empty : " — " + last.Message));
    }

    /// <summary>Модальное окно приложения (диалог) по AutomationId.</summary>
    public Window? FindDialog(string automationId = "ChoiceDialog")
    {
        foreach (var w in Window.ModalWindows)
        {
            if (w.AutomationId == automationId)
            {
                return w;
            }
        }

        var desktop = Automation.GetDesktop();
        foreach (var el in desktop.FindAllChildren(cf => cf.ByProcessId(App.ProcessId)))
        {
            if (el.AutomationId == automationId)
            {
                return el.AsWindow();
            }

            var inner = el.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (inner is not null && inner.ControlType == FlaUI.Core.Definitions.ControlType.Window)
            {
                return inner.AsWindow();
            }
        }

        return null;
    }

    public Window WaitDialog(string automationId = "ChoiceDialog", double seconds = 15)
    {
        Window? found = null;
        WaitUntil(() => (found = FindDialog(automationId)) is not null, seconds, "диалог " + automationId);
        return found!;
    }

    public void ClickInDialog(Window dialog, string buttonId)
    {
        var b = dialog.FindFirstDescendant(cf => cf.ByAutomationId(buttonId)) ?? throw new InvalidOperationException("Нет кнопки " + buttonId);
        b.AsButton().Invoke();
    }

    public List<string> TabNames()
    {
        var strip = Window.FindFirstDescendant(cf => cf.ByAutomationId("TabStrip"));
        if (strip is null)
        {
            return new List<string>();
        }

        return strip.AsListBox().Items.Select(i => i.Name).ToList();
    }

    public bool HasExited => App.HasExited;

    public void Dispose()
    {
        try
        {
            string log = Path.Combine(DataDir, "app.log");
            if (File.Exists(log))
            {
                Directory.CreateDirectory(Path.Combine(OutDir, "logs"));
                File.Copy(log, Path.Combine(OutDir, "logs", _name + ".log"), true);
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            _pipe.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            if (!App.HasExited)
            {
                App.Kill();
            }
        }
        catch
        {
            // ignore
        }

        App.Dispose();
        Automation.Dispose();
    }
}
