using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using ErpPrinter.Config;
using ErpPrinter.Config.Ui;
using ErpPrinter.Secrets;
using ErpPrinter.Settings;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ErpPrinter.Config.Tests;

/// <summary>An empty in-memory configuration.</summary>
internal sealed class TestConfig
{
    public MemoryRegistryStore Registry { get; } = new();
    public ConfigStore Store { get; }

    public TestConfig()
    {
        Store = new ConfigStore(Registry, new SecretStore(Registry, new InMemorySecretProtector()));
    }

    /// <summary>The registry values of the global key, for asserting what was written.</summary>
    public IReadOnlyDictionary<string, object> GlobalValues => Registry.GetValues(ConfigStore.RegistryRoot);

    public IReadOnlyDictionary<string, object> ProfileValues(string name) => Registry.GetValues($@"{ConfigStore.RegistryRoot}\Printers\{name}");

    public void AddOdooProfile(string name = "Send to Odoo", string apiKey = "key-1", IEnumerable<(string Key, object? Value)>? more = null)
    {
        var settings = new Dictionary<string, object?> { ["Backend"] = "OdooJson2", ["ServerUrl"] = "https://example.odoo.com", ["ApiKey"] = apiKey };
        foreach (var (key, value) in more ?? []) settings[key] = value;
        Store.CreateProfile(name, settings);
    }
}

/// <summary>Answers dialogs instead of showing them, and records what was asked.</summary>
internal sealed class FakeDialogs : IDialogs
{
    public List<(string Caption, string Text, MessageBoxButtons Buttons, MessageBoxIcon Icon)> Messages { get; } = [];
    public List<(string Title, string Text)> Reports { get; } = [];

    /// <summary>The answer to every question box (Yes/No/Cancel).</summary>
    public DialogResult Answer { get; set; } = DialogResult.Yes;

    /// <summary>The name typed into the Add/Rename box; null cancels it.</summary>
    public string? Name { get; set; }

    public DialogResult Message(IWin32Window owner, string text, string caption, MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
    {
        Messages.Add((caption, text, buttons, icon));
        return buttons == MessageBoxButtons.OK ? DialogResult.OK : Answer;
    }

    public string? PromptName(IWin32Window owner, string title, string prompt, string suggestion) => Name;

    public void ShowReport(IWin32Window owner, string title, string text) => Reports.Add((title, text));
}

internal sealed class FakeShell : IShell
{
    public List<string> Opened { get; } = [];
    public bool EventLogOpened { get; private set; }

    public void OpenFolder(string path) => Opened.Add(path);

    public void OpenEventLog() => EventLogOpened = true;
}

/// <summary>Runs test code on a fresh STA thread with a Windows Forms synchronization context and a message pump for awaits.</summary>
internal static class Sta
{
    private static readonly object InitLock = new();
    private static bool _initialized;

    public static void Run(Action body) => Run(() =>
    {
        body();
        return Task.CompletedTask;
    });

    public static void Run(Func<Task> body)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                lock (InitLock)
                {
                    if (!_initialized)
                    {
                        // The same settings as the generated ApplicationConfiguration.Initialize() of the Config project
                        // (this project generates its own copy, with other defaults): visual styles, per-monitor DPI, Segoe UI 9.
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                        Application.SetDefaultFont(new Font("Segoe UI", 9F));
                        _initialized = true;
                    }
                }
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                var task = body();
                WaitFor(() => task.IsCompleted, TimeSpan.FromSeconds(60));
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    /// <summary>Pumps messages until <paramref name="condition"/> holds.</summary>
    public static void WaitFor(Func<bool> condition, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > (timeout ?? TimeSpan.FromSeconds(10))) throw new TimeoutException("Condition was not met in time.");
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    public static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    public static string ScreenshotDirectory
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string Capture(Form form, string name)
    {
        Pump();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
        var path = Path.Combine(ScreenshotDirectory, name + ".png");
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }
}

internal static class ControlExtensions
{
    public static IEnumerable<Control> Descendants(this Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in child.Descendants()) yield return nested;
        }
    }

    public static T Find<T>(this Control root, Func<T, bool> match) where T : Control =>
        root.Descendants().OfType<T>().First(match);

    public static Button Button(this Control root, string text) => root.Find<Button>(b => b.Text == text);
}
