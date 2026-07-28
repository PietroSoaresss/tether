using Microsoft.UI.Xaml;
using Orchestration.App.Views;

namespace Orchestration.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "Orchestration";
        UpdateZoomLabel();

        // Empty canvas teaches nothing. Until a saved workspace exists, seed one of each.
        OnNewTerminal(this, new RoutedEventArgs());
        OnNewNote(this, new RoutedEventArgs());

        Closed += (_, _) =>
        {
            foreach (var node in _nodes)
                if (node.Node is TerminalNodeView terminal) terminal.DisposeSession();
        };
    }
}
