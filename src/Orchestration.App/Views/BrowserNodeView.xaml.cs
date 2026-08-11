using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Orchestration.Core.Models;
using Windows.System;

namespace Orchestration.App.Views;

public sealed partial class BrowserNodeView : UserControl, INodeView
{
    /// <summary>Chrome height in world units: 44 of identity plus 32 of navigation, one transform.</summary>
    private const double HeaderHeight = 76;

    /// <summary>
    /// Ctrl+wheel must reach the canvas, not Chromium's page zoom, so the page forwards it — the
    /// same contract the terminal's page keeps, and the same message shape.
    /// </summary>
    private const string WheelForwarder =
        "window.addEventListener('wheel', e => {" +
        "  if (!e.ctrlKey) return;" +
        "  e.preventDefault();" +
        "  window.chrome.webview.postMessage(JSON.stringify({ t: 'zoom', d: e.deltaY }));" +
        "}, { passive: false });";

    private double _lastZoom = 1;
    private bool _collapsed;
    private bool _selected;
    private string _url = "";

    public UIElement DragHandle => HeaderBar;
    public UIElement ConnectionSurface => ConnectionSurfaceElement;
    public UIElement ResizeGrip => ResizeGripElement;

    public event Action<BrowserNodeView>? CloseRequested;
    public event Action<BrowserNodeView>? UrlChanged;
    public event Action<double>? ZoomRequested;

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    /// <summary>The live address once the view is up; before that, whatever was assigned.</summary>
    public string Url
    {
        get => Web.CoreWebView2?.Source is { Length: > 0 } source && source != "about:blank"
            ? source
            : _url;
        set
        {
            _url = value;
            UrlBox.Text = value;
            if (Web.CoreWebView2 is not null) Navigate(value);
        }
    }

    public BrowserNodeView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        UrlBox.Text = _url;

        await Web.EnsureCoreWebView2Async(await Services.TetherWebView.SharedEnvironmentAsync());
        var core = Web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        // Chromium's own ctrl+wheel zoom would shadow the canvas gesture; the forwarder owns it.
        core.Settings.IsZoomControlEnabled = false;
        // This is a preview pane, not a browser product: the app owns one WebView2 user data folder
        // it never cleans and gives no UI to inspect or clear, so it must not collect the addresses
        // and passwords a browser node's remote pages would otherwise prompt to save into it.
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(WheelForwarder);

        core.WebMessageReceived += OnWebMessage;
        core.SourceChanged += (_, _) =>
        {
            // A redirect or an SPA pushState can land mid-edit; UrlChanged still fires so the model
            // tracks the real address, but the box itself is left alone while the user is typing.
            if (UrlBox.FocusState == FocusState.Unfocused) UrlBox.Text = core.Source;
            UrlChanged?.Invoke(this);
        };
        // CSS zoom does not survive a navigation, so every landing reapplies the current scale. Both
        // events are hooked: NavigationCompleted only fires once the whole load settles, so without
        // DOMContentLoaded every navigation paints at 1x and snaps, and a page that never fully
        // completes (a streaming response, a dev server holding the connection) would never zoom at
        // all. DOMContentLoaded fires before first paint, once documentElement exists.
        core.DOMContentLoaded += (_, _) => ApplyPageZoom();
        core.NavigationCompleted += (_, _) => ApplyPageZoom();
        // A popup that opened a real window would live outside the canvas; keep it in the node. The
        // host-initiated navigation this performs is not subject to the block WebView2 places on
        // web-content-initiated file:// navigations, so window.open('file:///...') is restricted to
        // http/https here rather than handed straight to Navigate.
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                core.Navigate(args.Uri);
        };

        // The address box lives inside the node's drag handle, so the canvas would otherwise read a
        // click here as the start of a drag and a double-click as "edit this node".
        UrlBox.PointerPressed += (_, e) => e.Handled = true;
        UrlBox.DoubleTapped += (_, e) => e.Handled = true;

        if (_url.Length > 0) Navigate(_url);
    }

    /// <summary>
    /// <see cref="TerminalNodeView"/> can parse its messages with the throwing accessors because its
    /// only possible sender is the app's own bundled term.local page. This view's sender is whatever
    /// site the user navigated to, and <c>window.chrome.webview</c> is reachable from every document
    /// a WebView2 loads — not just the one that got the wheel-forwarder script — so any remote page
    /// can call postMessage with an arbitrary payload. Nothing below may assume that payload's shape.
    /// </summary>
    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.TryGetWebMessageAsString());
            var message = document.RootElement;
            if (message.TryGetProperty("t", out var kind) && kind.GetString() == "zoom" &&
                message.TryGetProperty("d", out var delta) && delta.TryGetDouble(out double value))
                ZoomRequested?.Invoke(value);
        }
        catch (Exception e) when (
            e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            // A hostile or merely buggy page can post anything on this channel — a non-string
            // message, a JSON scalar instead of an object, a "d" that is missing or not a number.
            // None of that may throw out of a UI-thread event handler.
        }
    }

    public void Navigate(string text)
    {
        string url = BrowserNode.CompleteUrl(text);
        if (url.Length == 0 || Web.CoreWebView2 is null) return;
        try
        {
            Web.CoreWebView2.Navigate(url);
            UrlBox.ClearValue(Control.BorderBrushProperty);
        }
        catch (ArgumentException)
        {
            // Malformed address: stay on the current page, but say so — a control that silently
            // ignores the input is still wrong even though CompleteUrl now rejects far less of it.
            UrlBox.BorderBrush = (Brush)Application.Current.Resources["TetherDangerBrush"];
        }
    }

    private void OnUrlKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        Navigate(UrlBox.Text);
        e.Handled = true;
    }

    /// <summary>
    /// The nav row is visible and clickable while <see cref="OnLoaded"/> is still awaiting
    /// <c>EnsureCoreWebView2Async</c>, before <c>Web.CoreWebView2</c> exists, so these three guard the
    /// same way the rest of this file does.
    /// </summary>
    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (Web.CoreWebView2 is not null && Web.CanGoBack) Web.GoBack();
    }

    private void OnForward(object sender, RoutedEventArgs e)
    {
        if (Web.CoreWebView2 is not null && Web.CanGoForward) Web.GoForward();
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        if (Web.CoreWebView2 is not null) Web.Reload();
    }

    /// <summary>
    /// The body cannot scale as a surface (WebView2 is not composed by XAML), so the page content
    /// scales itself: CSS zoom on the root element, reapplied on every navigation. Chrome paints at
    /// <see cref="Camera.ChromeScale"/> like every other node.
    /// </summary>
    public void ApplyZoom(double zoom)
    {
        _lastZoom = zoom;
        SyncHeaderChrome();
        ApplyPageZoom();
    }

    private void ApplyPageZoom()
    {
        if (Web.CoreWebView2 is null) return;
        string zoom = _lastZoom.ToString(CultureInfo.InvariantCulture);
        _ = Web.CoreWebView2.ExecuteScriptAsync(
            $"document.documentElement.style.zoom='{zoom}'");
    }

    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e) => SyncHeaderChrome();

    /// <summary>
    /// The header is laid out at 1× and painted scaled, so its padding, badge and buttons track
    /// <see cref="Camera.ChromeScale"/> without a FontSize per element. The content box is the bar's
    /// device size divided back into world units — collapsed, that is the whole card. Unlike the
    /// sibling views, this header is two fixed rows rather than one row of vertically-centred
    /// children, so nothing here centres a label.
    /// </summary>
    private void SyncHeaderChrome()
    {
        if (_lastZoom <= 0) return;
        double scale = Camera.ChromeScale(_lastZoom);
        HeaderScale.ScaleX = HeaderScale.ScaleY = scale;
        HeaderContent.Width = Math.Max(HeaderBar.ActualWidth / scale, 0);
        HeaderContent.Height = _collapsed ? Math.Max(HeaderBar.ActualHeight / scale, 0) : HeaderHeight;
        if (!_collapsed) HeaderRow.Height = new GridLength(HeaderHeight * scale);
    }

    public void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        _collapsed = collapsed;

        // Header grows to fill the node so it stays the drag handle the canvas already hooked.
        HeaderRow.Height = collapsed
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(HeaderHeight * Camera.ChromeScale(_lastZoom));
        ContentRow.Height = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        HeaderActions.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        NavRow.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        // A zero-height WebView2 still composites; hiding it is what actually buys the frame time.
        Web.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SyncHeaderChrome();
    }

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this);

    /// <summary>
    /// Stops the page. Unlike the terminal's local xterm document, a browser node's page is remote
    /// code that keeps running — timers, media, sockets — until the control is finalized, which in a
    /// long session may be never.
    /// </summary>
    public void CloseWeb() => Web.Close();

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
