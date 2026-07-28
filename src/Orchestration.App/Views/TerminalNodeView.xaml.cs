using System.Text;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Orchestration.Core.Terminal;

namespace Orchestration.App.Views;

public sealed partial class TerminalNodeView : UserControl, INodeView
{
    // One browser process family for every terminal on the canvas.
    private static CoreWebView2Environment? _sharedEnvironment;
    private static readonly SemaphoreSlim EnvironmentLock = new(1, 1);

    private readonly DispatcherQueue _dispatcher;
    private readonly List<byte> _pendingOutput = new();
    private bool _flushScheduled;
    private bool _pageReady;
    private ConPtySession? _session;
    private double _baseFontSize = 14;
    private short _cols = 80, _rows = 24;

    public string CommandLine { get; set; } = "powershell.exe -NoLogo";
    public string? StartDirectory { get; set; }
    public UIElement DragHandle => HeaderBar;

    /// <summary>Raised for every chunk the child writes. This is the tap point the pipe engine will use.</summary>
    public event Action<byte[]>? OutputProduced;

    public event Action<TerminalNodeView>? CloseRequested;

    public TerminalNodeView()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        TitleText.Text = CommandLine;

        await EnvironmentLock.WaitAsync();
        try
        {
            _sharedEnvironment ??= await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: null,
                userDataFolder: Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Orchestration", "WebView2"),
                options: new CoreWebView2EnvironmentOptions());
        }
        finally
        {
            EnvironmentLock.Release();
        }

        await Web.EnsureCoreWebView2Async(_sharedEnvironment);

        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;

        core.SetVirtualHostNameToFolderMapping(
            "term.local",
            Path.Combine(AppContext.BaseDirectory, "Assets", "term"),
            CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessage;
        core.Navigate("https://term.local/index.html");
    }

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        JsonElement message;
        try { message = JsonDocument.Parse(args.TryGetWebMessageAsString()).RootElement; }
        catch (JsonException) { return; }

        switch (message.GetProperty("t").GetString())
        {
            case "ready":
                _pageReady = true;
                ReadSize(message);
                Start();
                break;

            case "size":
                ReadSize(message);
                _session?.Resize(_cols, _rows);
                break;

            case "i":
                var text = message.GetProperty("d").GetString();
                if (!string.IsNullOrEmpty(text)) _session?.Write(text);
                break;
        }
    }

    private void ReadSize(JsonElement message)
    {
        if (message.TryGetProperty("cols", out var c) && message.TryGetProperty("rows", out var r))
        {
            _cols = (short)Math.Max(1, c.GetInt32());
            _rows = (short)Math.Max(1, r.GetInt32());
        }
    }

    public void Start()
    {
        if (_session is not null || !_pageReady) return;

        var session = new ConPtySession();
        session.OutputReceived += OnSessionOutput;
        session.Exited += OnSessionExited;

        try
        {
            session.Start(CommandLine, StartDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), _cols, _rows);
        }
        catch (Exception ex)
        {
            session.Dispose();
            ShowExited($"Nao foi possivel iniciar:\n{ex.Message}");
            return;
        }

        _session = session;
        ExitOverlay.Visibility = Visibility.Collapsed;
        StateDot.Fill = new SolidColorBrush(Colors.LimeGreen);
    }

    private void OnSessionOutput(byte[] data)
    {
        OutputProduced?.Invoke(data);

        // Coalesce: while a flush is already queued, later chunks just pile onto the same batch.
        lock (_pendingOutput)
        {
            _pendingOutput.AddRange(data);
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        _dispatcher.TryEnqueue(FlushOutput);
    }

    private void FlushOutput()
    {
        byte[] batch;
        lock (_pendingOutput)
        {
            _flushScheduled = false;
            if (_pendingOutput.Count == 0) return;
            batch = _pendingOutput.ToArray();
            _pendingOutput.Clear();
        }

        if (Web.CoreWebView2 is null) return;
        Web.CoreWebView2.PostWebMessageAsString(
            JsonSerializer.Serialize(new { t = "o", d = Convert.ToBase64String(batch) }));
    }

    private void OnSessionExited(int code) =>
        _dispatcher.TryEnqueue(() => ShowExited($"Processo saiu (codigo {code})"));

    private void ShowExited(string message)
    {
        ExitText.Text = message;
        ExitOverlay.Visibility = Visibility.Visible;
        StateDot.Fill = new SolidColorBrush(Colors.OrangeRed);
    }

    /// <summary>Zoom is applied as layout, never as a transform: the WebView2 surface is not scalable.</summary>
    public void ApplyZoom(double zoom)
    {
        if (Web.CoreWebView2 is null || !_pageReady) return;
        Web.CoreWebView2.PostWebMessageAsString(
            JsonSerializer.Serialize(new { t = "font", size = Math.Clamp(_baseFontSize * zoom, 4, 48) }));
    }

    public void FocusTerminal() =>
        Web.CoreWebView2?.PostWebMessageAsString(JsonSerializer.Serialize(new { t = "focus" }));

    /// <summary>Writes into the child's stdin. This is what a connected upstream terminal will call.</summary>
    public void SendInput(string text) => _session?.Write(text);

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        DisposeSession();
        Start();
    }

    private void OnKill(object sender, RoutedEventArgs e) => _session?.Kill();

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DisposeSession();
        CloseRequested?.Invoke(this);
    }

    public void DisposeSession()
    {
        if (_session is null) return;
        _session.OutputReceived -= OnSessionOutput;
        _session.Exited -= OnSessionExited;
        _session.Dispose();
        _session = null;
        StateDot.Fill = new SolidColorBrush(Colors.Gray);
    }
}
