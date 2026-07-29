using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    private bool _selected;
    private ConPtySession? _session;
    private double _baseFontSize = 14;
    private double _lastZoom = 1;
    private string _fontFamily = "Cascadia Mono, Consolas, monospace";
    private string _title = "terminal";
    private short _cols = 80, _rows = 24;

    public string CommandLine { get; set; } = "powershell.exe -NoLogo";
    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            if (TitleText is not null) TitleText.Text = value;
        }
    }
    public string? StartDirectory { get; set; }
    public string? InitialInput { get; set; }
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; set; }
    public UIElement DragHandle => HeaderBar;
    public UIElement ConnectionSurface => ConnectionSurfaceElement;
    public UIElement ResizeGrip => ResizeGripElement;
    public bool IsRunning => _session?.State == SessionState.Running;

    /// <summary>Raised for every chunk the child writes. This is the tap point the pipe engine will use.</summary>
    public event Action<byte[]>? OutputProduced;
    public event Action<int>? ProcessExited;
    public event Action? Starting;

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
        TitleText.Text = Title;
        string command = CommandLine.ToLowerInvariant();
        KindText.Text = command.Contains("claude") ? "CLAUDE"
            : command.Contains("codex") ? "CODEX"
            : "TERMINAL";

        string directory = StartDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        ProjectText.Text = string.IsNullOrWhiteSpace(folder) ? directory : folder;
        ToolTipService.SetToolTip(ProjectText, directory);

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
                ApplyZoom(_lastZoom);
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
        Starting?.Invoke();

        var session = new ConPtySession();
        session.OutputReceived += OnSessionOutput;
        session.Exited += OnSessionExited;

        try
        {
            session.Start(
                CommandLine,
                StartDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                _cols,
                _rows,
                ExtraEnvironment);
        }
        catch (Exception ex)
        {
            session.Dispose();
            ShowExited($"Nao foi possivel iniciar:\n{ex.Message}");
            return;
        }

        _session = session;
        if (!string.IsNullOrWhiteSpace(InitialInput))
        {
            session.Write(InitialInput + "\r");
            InitialInput = null;
        }
        ExitOverlay.Visibility = Visibility.Collapsed;
        StateDot.Fill = (Brush)Application.Current.Resources["TetherAccentLimeBrush"];
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

    private void OnSessionExited(int code)
    {
        ProcessExited?.Invoke(code);
        _dispatcher.TryEnqueue(() => ShowExited($"Processo saiu (código {code})"));
    }

    private void ShowExited(string message)
    {
        ExitText.Text = message;
        ExitOverlay.Visibility = Visibility.Visible;
        StateDot.Fill = (Brush)Application.Current.Resources["TetherDangerBrush"];
    }

    /// <summary>Zoom is applied as layout, never as a transform: the WebView2 surface is not scalable.</summary>
    public void ApplyZoom(double zoom)
    {
        _lastZoom = zoom;
        if (Web.CoreWebView2 is null || !_pageReady) return;
        Web.CoreWebView2.PostWebMessageAsString(
            JsonSerializer.Serialize(new
            {
                t = "font",
                size = Math.Clamp(_baseFontSize * zoom, 12, 48),
                family = _fontFamily
            }));
    }

    public void ApplySettings(string family, double size)
    {
        _fontFamily = family;
        _baseFontSize = size;
        ApplyZoom(_lastZoom);
    }

    public void ApplyAccent(string? color)
    {
        if (color?.Length != 7 ||
            !uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            return;

        byte red = (byte)(rgb >> 16);
        byte green = (byte)(rgb >> 8);
        byte blue = (byte)rgb;
        HeaderBar.Background = new SolidColorBrush(
            Windows.UI.Color.FromArgb(58, red, green, blue));
        NodeBorder.BorderBrush = new SolidColorBrush(
            Windows.UI.Color.FromArgb(220, red, green, blue));
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

    public void RestartSession()
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
        StateDot.Fill = (Brush)Application.Current.Resources["TetherAccentVioletBrush"];
    }

    public void SetSelected(bool selected)
    {
        _selected = selected;
        SelectionRing.Opacity = selected ? 1 : 0;
        if (selected) HoverRing.Opacity = 0;
    }

    private void OnNodePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_selected) HoverRing.Opacity = 1;
    }

    private void OnNodePointerExited(object sender, PointerRoutedEventArgs e) =>
        HoverRing.Opacity = 0;
}
