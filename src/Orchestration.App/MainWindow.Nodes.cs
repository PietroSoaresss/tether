using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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

        /// <summary>
        /// The canvas this node lives on. Every node in the project is materialized at load, so
        /// removal and layout have to ask which tab owns a node rather than assume the active one.
        /// </summary>
        public required CanvasTab Tab;

        public double X { get => Model.X; set => Model.X = value; }
        public double Y { get => Model.Y; set => Model.Y = value; }
        public double Width { get => Model.Width; set => Model.Width = value; }
        public double Height { get => Model.Height; set => Model.Height = value; }
    }

    private readonly List<CanvasNode> _nodes = new();
    private double _spawnCursor;
    private CanvasNode? _selectedNode;

    /// <summary>Adds a node that already has a model — used both by the toolbar and by workspace load.</summary>
    private void AddNode(FrameworkElement view, INodeView node, NodeBase model, CanvasTab tab)
    {
        var entry = new CanvasNode { View = view, Node = node, Model = model, Tab = tab };

        _nodes.Add(entry);
        tab.Nodes.Add(model);
        World.Children.Add(view);
        // Load materializes every tab, so a node can be born onto a canvas that is not on screen.
        view.Visibility = ReferenceEquals(tab, _canvas) ? Visibility.Visible : Visibility.Collapsed;
        PlaceNode(entry);
        node.ApplyZoom(_zoom);
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
        if (ReferenceEquals(_selectedNode, entry)) SelectNode(null);
        _nodes.Remove(entry);
        entry.Tab.Nodes.Remove(entry.Model);
        entry.Tab.Connections.RemoveAll(c => c.SourceId == entry.Model.Id || c.TargetId == entry.Model.Id);
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
            SelectNode(entry);
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
            entry.X += (now.X - last.X) / _zoom;
            entry.Y += (now.Y - last.Y) / _zoom;
            last = now;
            PlaceNode(entry);
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

    private void SelectNode(CanvasNode? entry)
    {
        if (ReferenceEquals(_selectedNode, entry)) return;
        _selectedNode?.Node.SetSelected(false);
        _selectedNode = entry;
        _selectedNode?.Node.SetSelected(true);
        UpdateCanvasToolContext();
    }

    private string _shellKind = AgentKind.PowerShell.Id;
    private string _workingDirectory = "";

    private MenuFlyout? _terminalMenu;

    /// <summary>
    /// Builds the agent menu from the one table that knows the kinds, so adding an agent does not
    /// mean remembering to edit XAML too. One instance for every entry point that asks for a
    /// terminal — the toolbar, the canvas ruler and the keyboard shortcut.
    /// </summary>
    private MenuFlyout TerminalMenu()
    {
        if (_terminalMenu is not null) return _terminalMenu;

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };
        foreach (var kind in AgentKind.All)
        {
            var item = new MenuFlyoutItem
            {
                Text = kind.Label,
                Tag = kind.Id,
                Icon = new FontIcon { Glyph = kind.Glyph }
            };
            item.Click += OnPickShell;
            flyout.Items.Add(item);
        }
        return _terminalMenu = flyout;
    }

    /// <summary>
    /// Every route to a new terminal asks which agent. Creating with whatever was picked last is how
    /// pressing "Terminal" ends up silently launching a Claude.
    /// </summary>
    private void OnNewTerminal(object sender, RoutedEventArgs e) =>
        TerminalMenu().ShowAt(sender as FrameworkElement ?? NewTerminalButton);

    /// <summary>
    /// Every click opens the menu. It used to create straight away using whichever kind was picked
    /// last, so pressing "Terminal" could silently launch a Claude.
    /// </summary>
    private void OnPickShell(object sender, RoutedEventArgs e)
    {
        _shellKind = (string)((FrameworkElement)sender).Tag;
        ArmPlacement("place-terminal");
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

        _canvas = new CanvasTab();
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

    /// <summary>
    /// Checked before arming rather than after the drag: refusing at the end would make the user
    /// draw a rectangle only to be told it was never going to work.
    /// </summary>
    private void OnNewNote(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_workingDirectory))
        {
            ShowRecoveryNotice("Abra um projeto antes de criar uma nota.");
            return;
        }
        ArmPlacement("place-note");
    }

    private void CreateTerminal(
        double x,
        double y,
        double width = 720,
        double height = 420,
        string? title = null)
    {
        var kind = AgentKind.Find(_shellKind);
        Materialize(new TerminalNode
        {
            Title = title ?? kind.Label,
            X = x, Y = y, Width = width, Height = height,
            Kind = kind.Id,
            CommandLine = kind.CommandLine,
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

    private void CreateBrowser(double x, double y, double width = 720, double height = 480)
    {
        Materialize(new BrowserNode
        {
            Title = "navegador",
            X = x, Y = y, Width = width, Height = height
        });
    }

    private void OnNewBrowser(object sender, RoutedEventArgs e) => ArmPlacement("place-browser");

    /// <summary>Builds the view for a model. The one place that knows model kind maps to view kind.</summary>
    private void Materialize(NodeBase model, string? initialInput = null, CanvasTab? tab = null)
    {
        tab ??= _canvas;

        switch (model)
        {
            case TerminalNode terminalModel:
            {
                var view = new TerminalNodeView
                {
                    Title = terminalModel.Title,
                    Kind = terminalModel.Kind,
                    CommandLine = terminalModel.CommandLine,
                    StartDirectory = string.IsNullOrEmpty(terminalModel.WorkingDirectory) ? null : terminalModel.WorkingDirectory,
                    ExtraEnvironment = NodeEnvironment(terminalModel.Id),
                    InitialInput = initialInput
                };
                view.ApplySettings(
                    _settings.TerminalFontFamily, _settings.TerminalFontSize, _settings.SubmitGapMs);
                view.ApplyAccent(terminalModel.AccentColor);
                view.Starting += () => PrimeAgent(terminalModel);
                view.ZoomRequested += delta => ZoomAtNode(view, delta);
                view.CloseRequested += RemoveNode;
                _terminalViews[terminalModel.Id] = view;
                AddNode(view, view, model, tab);
                break;
            }
            case NoteNode noteModel:
            {
                var view = new NoteNodeView();
                BindNote(view, noteModel);
                view.CloseRequested += RemoveNode;
                AddNode(view, view, model, tab);
                break;
            }
            case BrowserNode browserModel:
            {
                var view = new BrowserNodeView
                {
                    Title = browserModel.Title,
                    Url = browserModel.Url
                };
                view.UrlChanged += changed =>
                {
                    browserModel.Url = changed.Url;
                    _autosave.Touch();
                };
                view.ZoomRequested += delta => ZoomAtNode(view, delta);
                view.CloseRequested += RemoveNode;
                AddNode(view, view, model, tab);
                break;
            }
            // LoadWorkspace clears the node list before materializing, so a kind that silently
            // fell through here would be dropped from memory and then erased from disk.
            default:
                throw new NotSupportedException($"No view for node kind {model.GetType().Name}.");
        }
    }
}
