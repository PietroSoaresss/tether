using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Orchestration.App.Views;
using Orchestration.Core.Models;
using Orchestration.Core.Persistence;
using Windows.Foundation;

namespace Orchestration.App;

/// <summary>Creating, removing and dragging the things on the canvas.</summary>
public sealed partial class MainWindow
{
    private sealed class CanvasNode
    {
        public required FrameworkElement View;
        public required INodeView Node;
        public required NodeBase Model;

        public double X { get => Model.X; set => Model.X = value; }
        public double Y { get => Model.Y; set => Model.Y = value; }
        public double Width { get => Model.Width; set => Model.Width = value; }
        public double Height { get => Model.Height; set => Model.Height = value; }
    }

    private readonly List<CanvasNode> _nodes = new();
    private readonly HashSet<CanvasNode> _selectedNodes = new();
    private double _spawnCursor;
    private CanvasNode? _selectedNode;

    /// <summary>Adds a node that already has a model — used both by the toolbar and by workspace load.</summary>
    private void AddNode(FrameworkElement view, INodeView node, NodeBase model)
    {
        var entry = new CanvasNode { View = view, Node = node, Model = model };

        _nodes.Add(entry);
        _workspace.Nodes.Add(model);
        World.Children.Add(view);
        PlaceNode(entry);
        node.ApplyZoom(_zoom);
        RenderWires();
        RegisterDrag(entry);
        RegisterGraphNode(entry);
        // AddNode serves both the toolbar and the load path; only the former is a user edit.
        if (!_loading) _autosave.Touch();
    }

    /// <summary>Stagger new nodes in world space so they do not land on top of each other.</summary>
    private (double X, double Y) NextSpawnPoint()
    {
        var point = ((40 + _spawnCursor * 28 - _offsetX) / _zoom, (40 + _spawnCursor * 28 - _offsetY) / _zoom);
        _spawnCursor = (_spawnCursor + 1) % 8;
        return point;
    }

    private void RemoveNode(FrameworkElement view)
    {
        var entry = _nodes.FirstOrDefault(n => ReferenceEquals(n.View, view));
        if (entry is null) return;

        if (ReferenceEquals(_wireSource, entry)) CancelWire();
        _nodes.Remove(entry);
        if (_selectedNodes.Remove(entry))
        {
            entry.Node.SetSelected(false);
            if (ReferenceEquals(_selectedNode, entry))
                _selectedNode = _nodes.LastOrDefault(_selectedNodes.Contains);
            UpdateCanvasToolContext();
        }
        _workspace.Nodes.Remove(entry.Model);
        _workspace.Connections.RemoveAll(c => c.SourceId == entry.Model.Id || c.TargetId == entry.Model.Id);
        _noteViews.Remove(entry.Model.Id);
        _terminalViews.Remove(entry.Model.Id);
        World.Children.Remove(view);
        RenderWires();
        _autosave.Touch();
    }

    private void RegisterDrag(CanvasNode entry)
    {
        var handle = entry.Node.DragHandle;
        Point last = default;
        bool dragging = false;

        handle.PointerPressed += (s, e) =>
        {
            if (!e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed) return;
            if (!_selectedNodes.Contains(entry)) SelectNode(entry);
            _selectedWire = null;
            RenderWires();
            last = e.GetCurrentPoint(Viewport).Position;
            dragging = ((UIElement)s).CapturePointer(e.Pointer);
            e.Handled = true;
        };

        handle.PointerMoved += (s, e) =>
        {
            if (!dragging) return;
            var now = e.GetCurrentPoint(Viewport).Position;
            double dx = (now.X - last.X) / _zoom;
            double dy = (now.Y - last.Y) / _zoom;
            last = now;
            foreach (CanvasNode selected in _selectedNodes)
            {
                selected.X += dx;
                selected.Y += dy;
                PlaceNode(selected);
            }
            RenderWires();
        };

        void EndDrag(object s, PointerRoutedEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            ((UIElement)s).ReleasePointerCapture(e.Pointer);
            _autosave.Touch();
        }

        handle.PointerReleased += EndDrag;
        handle.PointerCaptureLost += (s, e) =>
        {
            // PointerMoved has already written the new position into the model, so losing capture
            // to a focus steal still has to schedule the save that PointerReleased would have.
            if (!dragging) return;
            dragging = false;
            _autosave.Touch();
        };
    }

    private void SelectNode(CanvasNode? entry) =>
        SelectNodes(entry is null ? [] : [entry]);

    private void SelectNodes(IEnumerable<CanvasNode> entries)
    {
        var selected = entries.ToHashSet();
        foreach (CanvasNode node in _selectedNodes)
            if (!selected.Contains(node)) node.Node.SetSelected(false);
        foreach (CanvasNode node in selected)
            if (!_selectedNodes.Contains(node)) node.Node.SetSelected(true);

        _selectedNodes.Clear();
        _selectedNodes.UnionWith(selected);
        _selectedNode = _nodes.LastOrDefault(_selectedNodes.Contains);
        UpdateCanvasToolContext();
    }

    private string _shellKind = "powershell";
    private string _workingDirectory = "";

    /// <summary>
    /// Launched through the shell rather than directly, because CreateProcess with a null
    /// application name only finds .exe. On this machine `claude` happens to be claude.exe, but
    /// `codex` is codex.ps1 — a script CreateProcess cannot run at all, and npm .cmd shims are
    /// just as common. The shell resolves all three, and -NoExit keeps the prompt open so a
    /// missing CLI reports itself inside the terminal instead of vanishing.
    /// </summary>
    private static string CommandLineFor(string kind) => kind switch
    {
        "claude" => "powershell.exe -NoLogo -NoExit -Command claude",
        "codex" => "powershell.exe -NoLogo -NoExit -Command codex",
        _ => "powershell.exe -NoLogo"
    };

    private static string LabelFor(string kind) => kind switch
    {
        "claude" => "Novo Claude",
        "codex" => "Novo Codex",
        _ => "Novo terminal"
    };

    /// <summary>SplitButton.Click is a TypedEventHandler, not the RoutedEventHandler the rest use.</summary>
    private void OnNewTerminalClicked(SplitButton sender, SplitButtonClickEventArgs e) =>
        OnNewTerminal(sender, new RoutedEventArgs());

    private void OnPickShell(object sender, RoutedEventArgs e)
    {
        _shellKind = (string)((FrameworkElement)sender).Tag;
        NewTerminalLabel.Text = LabelFor(_shellKind);
        OnNewTerminal(sender, e);
    }

    private async void OnOpenProject(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        // WinUI 3 pickers are windowless COM objects; without an owner handle they throw.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        SwitchProject(folder.Path);
    }

    private void UpdateProjectLabel()
    {
        bool available = Directory.Exists(_workingDirectory);
        ProjectLabel.Text = available ? ProjectName(_workingDirectory) : "Abrir projeto";
        ToolTipService.SetToolTip(
            OpenProjectButton,
            available ? _workingDirectory : "Escolha a pasta do projeto");
        WatchProjectFiles();
        RefreshFileTree();
        RefreshProjectList();
    }

    private void SwitchProject(string projectDirectory)
    {
        string path = Path.GetFullPath(projectDirectory);
        if (Directory.Exists(_workingDirectory) && SamePath(_workingDirectory, path))
        {
            RememberProject(path);
            return;
        }

        SaveNow();
        ClearCanvasForProjectSwitch();

        _saveSuppressed = false;
        _workingDirectory = path;
        _store = new WorkspaceStore(ProjectPaths(path));
        _workspace = _store.Load();
        _workspace.ProjectDirectory = path;
        RememberProject(path);
        RestoreWorkspace(_store.LastLoadOutcome);
    }

    private void ClearCanvasForProjectSwitch()
    {
        CancelWire();
        SelectNode(null);
        _selectedWire = null;
        foreach (var terminal in _terminalViews.Values) terminal.DisposeSession();
        foreach (var watcher in _noteWatchers.Values) watcher.Dispose();
        _noteReload?.Cancel();
        _fileTreeWatcher?.Dispose();
        _fileTreeRefresh?.Cancel();

        _nodes.Clear();
        _terminalViews.Clear();
        _noteViews.Clear();
        _noteWatchers.Clear();
        _wirePaths.Clear();
        _annotationViews.Clear();
        _askGates.Clear();
        _askQueues.Clear();
        _activeChains.Clear();
        World.Children.Clear();
        Wires.Children.Clear();
        Annotations.Children.Clear();
        _spawnCursor = 0;
    }

    private void RememberProject(string projectDirectory)
    {
        string path = Path.GetFullPath(projectDirectory);
        _settings.RecentProjects.RemoveAll(recent => SamePath(recent, path));
        _settings.RecentProjects.Insert(0, path);
        if (_settings.RecentProjects.Count > 20)
            _settings.RecentProjects.RemoveRange(20, _settings.RecentProjects.Count - 20);
        _settings.LastProjectDirectory = path;
        try { _settingsStore.Save(_settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowRecoveryNotice($"Não foi possível salvar os projetos recentes: {e.Message}");
        }
        RefreshProjectList();
    }

    private void OnProjectFilterChanged(object sender, TextChangedEventArgs e) => RefreshProjectList();

    private void OnRecentProject(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && Directory.Exists(path))
            SwitchProject(path);
    }

    private void RefreshProjectList()
    {
        ProjectList.Children.Clear();
        string filter = ProjectFilter.Text.Trim();
        var projects = _settings.RecentProjects
            .Where(Directory.Exists)
            .Where(path => filter.Length == 0 ||
                           ProjectName(path).Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                           path.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .GroupBy(path => ProjectName(Directory.GetParent(path)?.FullName ?? path));

        int count = 0;
        foreach (var group in projects)
        {
            ProjectList.Children.Add(new TextBlock
            {
                Text = group.Key,
                Style = (Style)Application.Current.Resources["TetherProjectGroupStyle"]
            });

            foreach (string path in group)
            {
                bool active = Directory.Exists(_workingDirectory) && SamePath(path, _workingDirectory);
                var marker = new Border
                {
                    Width = 2,
                    Height = 18,
                    CornerRadius = new CornerRadius(1),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 194, 239, 78)),
                    Visibility = active ? Visibility.Visible : Visibility.Collapsed
                };
                var content = new Grid { ColumnSpacing = 8 };
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(marker, 0);
                content.Children.Add(marker);
                var icon = new FontIcon { Glyph = "\uE8B7", FontSize = 13 };
                Grid.SetColumn(icon, 1);
                content.Children.Add(icon);
                var name = new TextBlock
                {
                    Text = ProjectName(path),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12,
                    FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(name, 2);
                content.Children.Add(name);

                var button = new Button
                {
                    Tag = path,
                    Content = content,
                    Style = (Style)Application.Current.Resources["TetherProjectRowStyle"],
                    Background = active
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb(54, 121, 98, 140))
                        : new SolidColorBrush(Microsoft.UI.Colors.Transparent)
                };
                ToolTipService.SetToolTip(button, path);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                    button,
                    $"Abrir projeto {ProjectName(path)}");
                button.Click += OnRecentProject;
                ProjectList.Children.Add(button);
                count++;
            }
        }

        ProjectListEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProjectListEmpty.Text = filter.Length == 0
            ? "Abra uma pasta para começar."
            : "Nenhum projeto encontrado.";
    }

    private static string ProjectName(string path)
    {
        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private void OnNewTerminal(object sender, RoutedEventArgs e)
    {
        var (x, y) = NextSpawnPoint();
        CreateTerminal(x, y);
    }

    private void OnNewNote(object sender, RoutedEventArgs e)
    {
        var (x, y) = NextSpawnPoint();
        CreateNote(x, y);
    }

    private void CreateTerminal(
        double x,
        double y,
        double width = 720,
        double height = 420,
        string? title = null)
    {
        Materialize(new TerminalNode
        {
            Title = title ?? LabelFor(_shellKind),
            X = x, Y = y, Width = width, Height = height,
            CommandLine = CommandLineFor(_shellKind),
            WorkingDirectory = _workingDirectory
        });
    }

    private void CreateNote(double x, double y, double width = 340, double height = 240)
    {
        if (!Directory.Exists(_workingDirectory))
        {
            ShowRecoveryNotice("Abra um projeto antes de criar uma nota.");
            return;
        }

        var model = new NoteNode
        {
            Title = "nota",
            X = x, Y = y, Width = width, Height = height,
            WorkingDirectory = _workingDirectory,
            ViewMode = NoteViewMode.Raw
        };
        try
        {
            string folder = NoteFolder(model)!;
            model.FileName = _noteFiles.CreateUniqueName("nota", folder);
            _noteFiles.Write(model.FileName, "# Nota\n\nTexto em markdown.", folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowRecoveryNotice($"Não foi possível criar a nota: {e.Message}");
            return;
        }
        Materialize(model);
        RefreshFileTree();
    }

    /// <summary>Builds the view for a model. The one place that knows model kind maps to view kind.</summary>
    private void Materialize(NodeBase model, string? initialInput = null)
    {
        switch (model)
        {
            case TerminalNode terminalModel:
            {
                var view = new TerminalNodeView
                {
                    Title = terminalModel.Title,
                    CommandLine = terminalModel.CommandLine,
                    StartDirectory = string.IsNullOrEmpty(terminalModel.WorkingDirectory) ? null : terminalModel.WorkingDirectory,
                    ExtraEnvironment = NodeEnvironment(terminalModel.Id),
                    InitialInput = initialInput
                };
                view.ApplySettings(_settings.TerminalFontFamily, _settings.TerminalFontSize);
                view.ApplyAccent(terminalModel.AccentColor);
                view.Starting += () => PrimeAgent(terminalModel);
                view.CloseRequested += RemoveNode;
                _terminalViews[terminalModel.Id] = view;
                AddNode(view, view, model);
                break;
            }
            case NoteNode noteModel:
            {
                var view = new NoteNodeView();
                BindNote(view, noteModel);
                view.CloseRequested += RemoveNode;
                AddNode(view, view, model);
                break;
            }
            // LoadWorkspace clears the node list before materializing, so a kind that silently
            // fell through here would be dropped from memory and then erased from disk.
            default:
                throw new NotSupportedException($"No view for node kind {model.GetType().Name}.");
        }
    }
}
