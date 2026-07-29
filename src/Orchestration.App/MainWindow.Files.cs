using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Orchestration.App;

internal sealed class FileTreeItem
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public override string ToString() => Name;
}

public sealed partial class MainWindow
{
    private FileSystemWatcher? _fileTreeWatcher;
    private CancellationTokenSource? _fileTreeRefresh;

    private void RefreshFileTree()
    {
        FileTree.RootNodes.Clear();
        bool available = Directory.Exists(_workingDirectory);
        ExplorerPathText.Text = available ? _workingDirectory : "Nenhum projeto";
        FileTreeEmptyText.Text = string.IsNullOrWhiteSpace(_workingDirectory)
            ? "Abra um projeto para ver os arquivos."
            : "A pasta do projeto não está disponível.";
        ToolTipService.SetToolTip(ExplorerPathText, available ? _workingDirectory : null);
        FileTree.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        FileTreeEmptyText.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        if (!available) return;

        var root = CreateFileTreeNode(_workingDirectory);
        FileTree.RootNodes.Add(root);
        LoadFileTreeChildren(root);
        root.IsExpanded = true;
    }

    private static TreeViewNode CreateFileTreeNode(string path)
    {
        bool directory = Directory.Exists(path);
        var node = new TreeViewNode
        {
            Content = new FileTreeItem
            {
                Name = ProjectName(path),
                FullPath = path,
                IsDirectory = directory
            }
        };

        if (directory)
        {
            try { node.HasUnrealizedChildren = Directory.EnumerateFileSystemEntries(path).Any(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return node;
    }

    private static void LoadFileTreeChildren(TreeViewNode node)
    {
        if (node.Content is not FileTreeItem { IsDirectory: true } item) return;
        node.Children.Clear();
        node.HasUnrealizedChildren = false;

        try
        {
            foreach (string path in Directory.EnumerateDirectories(item.FullPath)
                         .Where(ShowInFileTree)
                         .OrderBy(ProjectName, StringComparer.CurrentCultureIgnoreCase))
                node.Children.Add(CreateFileTreeNode(path));
            foreach (string path in Directory.EnumerateFiles(item.FullPath)
                         .Where(ShowInFileTree)
                         .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase))
                node.Children.Add(CreateFileTreeNode(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            node.Children.Add(new TreeViewNode
            {
                Content = new FileTreeItem
                {
                    Name = "Sem acesso",
                    FullPath = item.FullPath,
                    IsDirectory = false
                }
            });
        }
    }

    private void OnFileTreeExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.HasUnrealizedChildren) LoadFileTreeChildren(args.Node);
    }

    private void OnRefreshFiles(object sender, RoutedEventArgs e) => RefreshFileTree();

    private void WatchProjectFiles()
    {
        _fileTreeWatcher?.Dispose();
        _fileTreeWatcher = null;
        if (!Directory.Exists(_workingDirectory)) return;

        try
        {
            var watcher = new FileSystemWatcher(_workingDirectory, "*")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };
            watcher.Created += OnProjectTreeChanged;
            watcher.Deleted += OnProjectTreeChanged;
            watcher.Renamed += OnProjectTreeChanged;
            _fileTreeWatcher = watcher;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowRecoveryNotice($"Não foi possível acompanhar os arquivos do projeto: {e.Message}");
        }
    }

    private void OnProjectTreeChanged(object sender, FileSystemEventArgs e)
    {
        if (!ShowInFileTree(e.FullPath) ||
            e is RenamedEventArgs renamed && !ShowInFileTree(renamed.OldFullPath))
            return;

        _fileTreeRefresh?.Cancel();
        var pending = _fileTreeRefresh = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, pending.Token);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ExplorerPanel.Visibility == Visibility.Visible) RefreshFileTree();
                });
            }
            catch (OperationCanceledException) { }
        });
    }

    private static bool ShowInFileTree(string path)
    {
        string name = Path.GetFileName(path);
        return !name.Equals(".tether", StringComparison.OrdinalIgnoreCase) &&
               !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
               !name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);
    }

    private void OnToggleExplorer(object sender, RoutedEventArgs e)
    {
        bool show = ExplorerPanel.Visibility != Visibility.Visible;
        ExplorerPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ExplorerColumn.Width = new GridLength(show ? 288 : 0);
        if (show) RefreshFileTree();
    }
}
