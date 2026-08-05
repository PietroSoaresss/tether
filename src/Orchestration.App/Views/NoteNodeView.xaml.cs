using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Orchestration.Core.Models;

namespace Orchestration.App.Views;

public sealed partial class NoteNodeView : UserControl, INodeView
{
    /// <summary>Header height in world units; on screen it is this times the zoom.</summary>
    private const double HeaderHeight = 44;

    private double _baseFontSize = 13;
    private double _lastZoom = 1;
    private bool _collapsed;
    private bool _selected;
    private bool _setting;
    private NoteViewMode _viewMode = NoteViewMode.Preview;

    public UIElement DragHandle => HeaderBar;
    public UIElement ConnectionSurface => ConnectionSurfaceElement;
    public UIElement ResizeGrip => ResizeGripElement;

    public event Action<NoteNodeView>? CloseRequested;
    public event Action<NoteNodeView>? MarkdownChanged;
    public event Action<NoteNodeView, NoteViewMode>? ViewModeChanged;
    public event Action<NoteNodeView>? RecreateRequested;

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public string Markdown
    {
        get => Editor.Text;
        set
        {
            if (Editor.Text == value) return;
            _setting = true;
            Editor.Text = value;
            _setting = false;
            RenderMarkdown();
        }
    }

    public NoteNodeView() => InitializeComponent();

    public NoteViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            _viewMode = value;
            PreviewToggle.IsChecked = value == NoteViewMode.Preview;
            Editor.Visibility = value == NoteViewMode.Raw ? Visibility.Visible : Visibility.Collapsed;
            PreviewScroller.Visibility = value == NoteViewMode.Preview ? Visibility.Visible : Visibility.Collapsed;
            if (value == NoteViewMode.Preview) RenderMarkdown();
        }
    }

    public bool FileMissing
    {
        set
        {
            MissingPanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            Editor.IsEnabled = !value;
            if (value) PreviewScroller.Visibility = Visibility.Collapsed;
            else ViewMode = _viewMode;
        }
    }

    /// <summary>
    /// Text tracks the scale exactly. The old <c>Clamp(…, 12, 48)</c> froze it below zoom 0.92 and
    /// above 3.7, which is what made the note stop matching its own box; staying readable when the
    /// node is tiny is the collapsed card's job, not a clamp's.
    /// </summary>
    public void ApplyZoom(double zoom)
    {
        _lastZoom = zoom;
        double size = Camera.FontSize(_baseFontSize, zoom);
        Editor.FontSize = size;
        Preview.FontSize = size;
        Editor.Padding = new Thickness(14 * zoom, 12 * zoom, 14 * zoom, 12 * zoom);
        PreviewScroller.Padding = Editor.Padding;
        SyncHeaderChrome();
    }

    public void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        _collapsed = collapsed;

        // Header grows to fill the node so it stays the drag handle the canvas already hooked.
        HeaderRow.Height = collapsed ? new GridLength(1, GridUnitType.Star) : new GridLength(HeaderHeight * _lastZoom);
        ContentRow.Height = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        HeaderActions.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SyncHeaderChrome();
    }

    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e) => SyncHeaderChrome();

    /// <summary>
    /// The header is laid out at 1× and painted scaled, so its padding, badge and buttons track the
    /// zoom without a FontSize per element. Collapsed it is a card rather than a scaled view, and
    /// stays at device size — a card scaled to 20% would defeat the point of collapsing.
    /// </summary>
    private void SyncHeaderChrome()
    {
        if (_lastZoom <= 0) return;
        double scale = _collapsed ? 1 : _lastZoom;
        HeaderScale.ScaleX = HeaderScale.ScaleY = scale;
        // NaN is Auto: collapsed, the content stretches into the star-sized row as it always did.
        HeaderContent.Width = _collapsed ? double.NaN : Math.Max(HeaderBar.ActualWidth / scale, 0);
        HeaderContent.Height = _collapsed ? double.NaN : HeaderHeight;
        if (!_collapsed) HeaderRow.Height = new GridLength(HeaderHeight * _lastZoom);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_setting) return;
        RenderMarkdown();
        MarkdownChanged?.Invoke(this);
    }

    private void OnTogglePreview(object sender, RoutedEventArgs e)
    {
        ViewMode = PreviewToggle.IsChecked == true ? NoteViewMode.Preview : NoteViewMode.Raw;
        ViewModeChanged?.Invoke(this, ViewMode);
    }

    private void RenderMarkdown()
    {
        Preview.Blocks.Clear();
        foreach (string source in Markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = source;
            var paragraph = new Paragraph();
            if (line.StartsWith("### ")) { paragraph.FontSize = Preview.FontSize * 1.15; line = line[4..]; }
            else if (line.StartsWith("## ")) { paragraph.FontSize = Preview.FontSize * 1.3; line = line[3..]; }
            else if (line.StartsWith("# ")) { paragraph.FontSize = Preview.FontSize * 1.55; line = line[2..]; }
            else if (line.StartsWith("- ")) line = "• " + line[2..];
            AddInline(paragraph, line);
            Preview.Blocks.Add(paragraph);
        }
    }

    private static void AddInline(Paragraph paragraph, string text)
    {
        int index = 0;
        while (index < text.Length)
        {
            int bold = text.IndexOf("**", index, StringComparison.Ordinal);
            int code = text.IndexOf('`', index);
            int next = new[] { bold, code }.Where(i => i >= 0).DefaultIfEmpty(-1).Min();
            if (next < 0) { paragraph.Inlines.Add(new Run { Text = text[index..] }); break; }
            if (next > index) paragraph.Inlines.Add(new Run { Text = text[index..next] });

            string marker = next == bold ? "**" : "`";
            int end = text.IndexOf(marker, next + marker.Length, StringComparison.Ordinal);
            if (end < 0) { paragraph.Inlines.Add(new Run { Text = text[next..] }); break; }

            var run = new Run { Text = text[(next + marker.Length)..end] };
            if (marker == "**") run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            else run.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            paragraph.Inlines.Add(run);
            index = end + marker.Length;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this);
    private void OnRecreate(object sender, RoutedEventArgs e) => RecreateRequested?.Invoke(this);

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
