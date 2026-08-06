using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Orchestration.App.Views;
using Orchestration.Core.Models;

namespace Orchestration.App;

/// <summary>
/// Several canvases per project. Switching hides the other tabs' node views instead of tearing them
/// down: a terminal on another canvas is a running process, and the point of the product is that it
/// keeps working while you look somewhere else.
/// </summary>
public sealed partial class MainWindow
{
    private CanvasTab _canvas = new();

    /// <summary>Nodes on the canvas currently on screen. The rest exist but are hidden.</summary>
    private IEnumerable<CanvasNode> ActiveNodes() =>
        _nodes.Where(node => ReferenceEquals(node.Tab, _canvas));

    /// <summary>
    /// Every node in the project, on any canvas. The CLI and the agent protocol address nodes by id
    /// and neither knows nor should know which tab is on screen.
    /// </summary>
    private IEnumerable<NodeBase> AllNodes() => _workspace.Tabs.SelectMany(tab => tab.Nodes);

    private CanvasTab TabOf(Guid nodeId) =>
        _workspace.Tabs.FirstOrDefault(tab => tab.Nodes.Any(node => node.Id == nodeId)) ?? _canvas;

    private void RefreshTabStrip()
    {
        TabStrip.Children.Clear();
        foreach (var tab in _workspace.Tabs)
        {
            bool active = ReferenceEquals(tab, _canvas);
            var button = new Button
            {
                Tag = tab.Id,
                Content = new TextBlock
                {
                    Text = tab.Name,
                    MaxWidth = 140,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = 12,
                    FontWeight = active
                        ? Microsoft.UI.Text.FontWeights.SemiBold
                        : Microsoft.UI.Text.FontWeights.Normal
                },
                Style = (Style)Application.Current.Resources["TetherNodeActionButtonStyle"],
                Padding = new Thickness(10, 5, 10, 5),
                Background = active
                    ? (Brush)Application.Current.Resources["TetherAccentLimeBrush"]
                    : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Foreground = active
                    ? (Brush)Application.Current.Resources["TetherAccentOnLimeBrush"]
                    : (Brush)Application.Current.Resources["TetherTextSecondaryBrush"]
            };
            ToolTipService.SetToolTip(button, "Clique para abrir, botão direito para renomear ou fechar");
            button.Click += (_, _) => SwitchTab(tab);
            button.RightTapped += (_, e) =>
            {
                ShowTabMenu(tab, button);
                e.Handled = true;
            };
            TabStrip.Children.Add(button);
        }
    }

    private void SwitchTab(CanvasTab tab)
    {
        if (ReferenceEquals(tab, _canvas)) return;
        // The camera belongs to the canvas being left, so it has to be written before the swap.
        SaveCamera();
        ActivateTab(tab);
    }

    /// <summary>
    /// Puts a canvas on screen. Split from <see cref="SwitchTab"/> because closing the active tab
    /// has no canvas left to save a camera into.
    /// </summary>
    private void ActivateTab(CanvasTab tab)
    {
        CancelWire();
        SelectNode(null);
        SelectAnnotation(null);
        _selectedWire = null;

        _canvas = tab;
        _workspace.ActiveTabId = tab.Id;
        _offsetX = tab.Camera.OffsetX;
        _offsetY = tab.Camera.OffsetY;
        _zoom = tab.Camera.Zoom;

        ShowActiveTabNodes();
        RefreshTabStrip();
        ApplyLayout();
        _autosave.Touch();
    }

    /// <summary>
    /// Hidden, not removed. A WebView2 with no session behind it is what a rebuild would cost, and
    /// the collapsed-node path already proves hiding one is enough to stop paying for its frames.
    /// </summary>
    private void ShowActiveTabNodes()
    {
        foreach (var node in _nodes)
            node.View.Visibility = ReferenceEquals(node.Tab, _canvas)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void OnNewTab(object sender, RoutedEventArgs e)
    {
        var tab = new CanvasTab { Name = $"Canvas {_workspace.Tabs.Count + 1}" };
        _workspace.Tabs.Add(tab);
        SwitchTab(tab);
    }

    private void ShowTabMenu(CanvasTab tab, FrameworkElement target)
    {
        var rename = new MenuFlyoutItem { Text = "Renomear" };
        rename.Click += async (_, _) => await RenameTab(tab);

        var close = new MenuFlyoutItem
        {
            Text = "Fechar",
            // The last canvas has nowhere to fall back to, and an empty tab strip has no way back.
            IsEnabled = _workspace.Tabs.Count > 1
        };
        close.Click += async (_, _) => await CloseTab(tab);

        var menu = new MenuFlyout();
        menu.Items.Add(rename);
        menu.Items.Add(close);
        menu.ShowAt(target);
    }

    private async Task RenameTab(CanvasTab tab)
    {
        var name = new TextBox { Header = "Nome", Text = tab.Name, SelectionStart = tab.Name.Length };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Renomear canvas",
            Content = name,
            PrimaryButtonText = "Salvar",
            CloseButtonText = "Cancelar"
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string trimmed = name.Text.Trim();
        if (trimmed.Length == 0) return;
        tab.Name = trimmed;
        RefreshTabStrip();
        _autosave.Touch();
    }

    private async Task CloseTab(CanvasTab tab)
    {
        if (_workspace.Tabs.Count <= 1) return;

        // Closing takes running processes with it, which is not something to discover afterwards.
        if (tab.Nodes.Count > 0)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = $"Fechar {tab.Name}?",
                Content = $"{tab.Nodes.Count} nó(s) serão removidos e os terminais desse canvas encerrados.",
                PrimaryButtonText = "Fechar canvas",
                CloseButtonText = "Cancelar"
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        foreach (var node in _nodes.Where(node => ReferenceEquals(node.Tab, tab)).ToList())
        {
            if (node.Node is TerminalNodeView terminal) terminal.DisposeSession();
            _nodes.Remove(node);
            _noteViews.Remove(node.Model.Id);
            _terminalViews.Remove(node.Model.Id);
            World.Children.Remove(node.View);
        }

        int index = _workspace.Tabs.IndexOf(tab);
        bool wasActive = ReferenceEquals(_canvas, tab);
        _workspace.Tabs.Remove(tab);

        if (wasActive) ActivateTab(_workspace.Tabs[Math.Min(index, _workspace.Tabs.Count - 1)]);
        else RefreshTabStrip();
        _autosave.Touch();
    }
}
