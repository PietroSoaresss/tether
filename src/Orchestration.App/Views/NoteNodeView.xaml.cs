using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Orchestration.Core.Models;

namespace Orchestration.App.Views;

public sealed partial class NoteNodeView : UserControl, INodeView
{
    private double _baseFontSize = 13;
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

    public void ApplyZoom(double zoom)
    {
        double size = Math.Clamp(_baseFontSize * zoom, 12, 48);
        Editor.FontSize = size;
        Preview.FontSize = size;
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
