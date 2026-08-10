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
using Orchestration.Core.Models;
using Orchestration.Core.Terminal;

namespace Orchestration.App.Views;

public sealed partial class TerminalNodeView : UserControl, INodeView
{
    /// <summary>Header height in device pixels — fixed at every zoom.</summary>
    private const double HeaderHeight = 44;

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
    private int _submitGapMs = 120;
    private double _lastZoom = 1;
    private string _fontFamily = "Cascadia Mono, Consolas, monospace";
    private string _kind = AgentKind.PowerShell.Id;
    private string _title = "terminal";
    private bool _collapsed;
    private short _cols = 80, _rows = 24;

    public string CommandLine { get; set; } = "powershell.exe -NoLogo";

    /// <summary>Drives the badge and the glyph. Falls back to the command line for older nodes.</summary>
    public string Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            if (KindText is not null) ApplyKind();
        }
    }

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

    /// <summary>Ctrl+wheel landed on the web content. The argument is the page's wheel delta.</summary>
    public event Action<double>? ZoomRequested;

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
        ApplyKind();

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
        // Browser page zoom on a node is never what the user meant: they were zooming the canvas
        // and the pointer happened to be over a terminal. The page forwards the gesture instead.
        core.Settings.IsZoomControlEnabled = false;

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

            case "zoom":
                ZoomRequested?.Invoke(message.GetProperty("d").GetDouble());
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
            // Not awaited: Start runs on the UI thread from a page message, and the gap before
            // the Enter is the point. Failures land in SendInput, which already swallows a dead pipe.
            _ = SendPrompt(InitialInput);
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

    /// <summary>
    /// Zoom reaches the body as layout, never as a transform: the WebView2 surface is not scalable.
    /// The font tracks the same scale through <see cref="Camera.FontSize"/>, which is what keeps the
    /// pseudoconsole's column count constant across the zoom range — see the note there.
    /// The header does not participate: chrome is identity, fixed at device size in XAML.
    /// Columns stay constant because width has no fixed subtraction, but rows do not: the body height
    /// is node height times zoom minus the fixed header, so the page re-fits and resizes the pty as you
    /// zoom — the price of chrome that no longer scales.
    /// </summary>
    public void ApplyZoom(double zoom)
    {
        _lastZoom = zoom;
        if (Web.CoreWebView2 is null || !_pageReady) return;
        Web.CoreWebView2.PostWebMessageAsString(
            JsonSerializer.Serialize(new
            {
                t = "font",
                size = Camera.FontSize(_baseFontSize, zoom),
                family = _fontFamily
            }));
    }

    private void ApplyKind()
    {
        var kind = AgentKind.All.FirstOrDefault(k => k.Id == _kind) ?? AgentKind.FromCommandLine(CommandLine);
        KindText.Text = kind.Badge;
        KindIcon.Glyph = kind.Glyph;
    }

    public void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        _collapsed = collapsed;

        // The header grows to fill the node instead of a separate card, so it stays the drag handle
        // the canvas registered at creation time.
        HeaderRow.Height = collapsed
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(HeaderHeight);
        ContentRow.Height = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        HeaderActions.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ProjectText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        // A zero-height WebView2 still composites; hiding it is what actually buys the frame time.
        Web.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ApplySettings(string family, double size, int submitGapMs)
    {
        _fontFamily = family;
        _baseFontSize = size;
        _submitGapMs = submitGapMs;
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

    /// <summary>
    /// Hands a delegated prompt to the agent and submits it. The Enter is a separate write on
    /// purpose — see <see cref="PromptSubmission"/>, which owns that reasoning.
    /// </summary>
    public Task SendPrompt(string prompt) =>
        PromptSubmission.Send(SendInput, prompt, TimeSpan.FromMilliseconds(_submitGapMs));

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
