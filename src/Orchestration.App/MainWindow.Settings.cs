using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Orchestration.Core.Models;
using Windows.System;

namespace Orchestration.App;

public sealed partial class MainWindow
{
    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        var theme = new ComboBox
        {
            Header = "Tema",
            ItemsSource = Enum.GetNames<AppTheme>(),
            SelectedItem = _settings.Theme.ToString()
        };
        var family = new TextBox { Header = "Fonte do terminal", Text = _settings.TerminalFontFamily };
        var size = new NumberBox
        {
            Header = "Tamanho da fonte",
            Minimum = 8, Maximum = 32, Value = _settings.TerminalFontSize,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var idle = new NumberBox
        {
            Header = "Ociosidade do ask (ms)",
            Minimum = 200, Maximum = 30000, Value = _settings.IdleMs
        };
        var timeout = new NumberBox
        {
            Header = "Timeout do ask (ms)",
            Minimum = 1000, Maximum = 600000, Value = _settings.AskTimeoutMs
        };
        var depth = new NumberBox
        {
            Header = "Profundidade maxima",
            Minimum = 1, Maximum = 20, Value = _settings.MaxCallDepth
        };
        var primer = new ToggleSwitch
        {
            Header = "Atualizar instrucoes em AGENTS.md",
            IsOn = _settings.SeedAgentInstructions
        };
        var shortcuts = DefaultShortcuts().ToDictionary(
            pair => pair.Key,
            pair => new TextBox
            {
                Header = $"Atalho: {pair.Key}",
                Text = _settings.Shortcuts.GetValueOrDefault(pair.Key, pair.Value)
            });

        static TextBlock Section(string text) => new()
        {
            Text = text,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Section("Geral"));
        panel.Children.Add(theme);
        panel.Children.Add(Section("Terminal"));
        panel.Children.Add(family);
        panel.Children.Add(size);
        panel.Children.Add(Section("Agentes"));
        panel.Children.Add(idle);
        panel.Children.Add(timeout);
        panel.Children.Add(depth);
        panel.Children.Add(primer);
        panel.Children.Add(Section("Atalhos"));
        foreach (var field in shortcuts.Values) panel.Children.Add(field);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Configurações",
            Content = new ScrollViewer { Content = panel, MaxHeight = 600 },
            PrimaryButtonText = "Salvar",
            CloseButtonText = "Cancelar"
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var gestures = shortcuts.ToDictionary(pair => pair.Key, pair => pair.Value.Text.Trim());
        var duplicates = gestures.Values
            .Where(value => value.Length > 0)
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null)
        {
            ShowRecoveryNotice($"Atalho repetido: {duplicates.Key}");
            return;
        }

        _settings.Theme = Enum.Parse<AppTheme>((string)theme.SelectedItem);
        _settings.TerminalFontFamily = family.Text.Trim();
        _settings.TerminalFontSize = size.Value;
        _settings.IdleMs = (int)idle.Value;
        _settings.AskTimeoutMs = (int)timeout.Value;
        _settings.MaxCallDepth = (int)depth.Value;
        _settings.SeedAgentInstructions = primer.IsOn;
        _settings.Shortcuts = gestures;
        _settingsStore.Save(_settings);
        ApplySettings();
    }

    private void ApplySettings()
    {
        RootGrid.RequestedTheme = _settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        foreach (var terminal in _terminalViews.Values)
        {
            terminal.ApplySettings(_settings.TerminalFontFamily, _settings.TerminalFontSize);
            terminal.ApplyZoom(_zoom);
        }
        ConfigureAccelerators();
    }

    private void ConfigureAccelerators()
    {
        RootGrid.KeyboardAccelerators.Clear();
        foreach (var (action, fallback) in DefaultShortcuts())
        {
            string gesture = _settings.Shortcuts.GetValueOrDefault(action, fallback);
            if (!TryParseGesture(gesture, out var key, out var modifiers)) continue;
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                RunShortcut(action);
                args.Handled = true;
            };
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }
    }

    private void RunShortcut(string action)
    {
        switch (action)
        {
            case "novo-terminal": OnNewTerminal(this, new RoutedEventArgs()); break;
            case "nova-nota": OnNewNote(this, new RoutedEventArgs()); break;
            case "ajustar-zoom": OnResetView(this, new RoutedEventArgs()); break;
            case "deletar": DeleteSelection(); break;
            case "reiniciar-terminal":
                if (_selectedNode?.Node is Views.TerminalNodeView terminal) terminal.RestartSession();
                break;
        }
    }

    private void DeleteSelection()
    {
        if (_selectedWire is Guid wire)
        {
            DeleteWire(wire);
            return;
        }
        if (_selectedNode is not null)
        {
            var selected = _selectedNode;
            SelectNode(null);
            if (selected.Node is Views.TerminalNodeView terminal) terminal.DisposeSession();
            RemoveNode(selected.View);
        }
    }

    private static Dictionary<string, string> DefaultShortcuts() => new()
    {
        ["novo-terminal"] = "Ctrl+T",
        ["nova-nota"] = "Ctrl+N",
        ["ajustar-zoom"] = "Ctrl+Number0",
        ["deletar"] = "Delete",
        ["reiniciar-terminal"] = "Ctrl+R"
    };

    private static bool TryParseGesture(
        string gesture,
        out VirtualKey key,
        out VirtualKeyModifiers modifiers)
    {
        key = VirtualKey.None;
        modifiers = VirtualKeyModifiers.None;
        string[] parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !Enum.TryParse(parts[^1], ignoreCase: true, out key)) return false;
        foreach (string part in parts[..^1])
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) modifiers |= VirtualKeyModifiers.Control;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= VirtualKeyModifiers.Shift;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= VirtualKeyModifiers.Menu;
            else return false;
        }
        return true;
    }
}
