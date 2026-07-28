using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Orchestration.App.Views;

public sealed partial class NoteNodeView : UserControl, INodeView
{
    private double _baseFontSize = 13;

    public UIElement DragHandle => HeaderBar;

    public event Action<NoteNodeView>? CloseRequested;

    public string Markdown
    {
        get => Editor.Text;
        set => Editor.Text = value;
    }

    public NoteNodeView() => InitializeComponent();

    public void ApplyZoom(double zoom) => Editor.FontSize = Math.Clamp(_baseFontSize * zoom, 4, 48);

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this);
}
