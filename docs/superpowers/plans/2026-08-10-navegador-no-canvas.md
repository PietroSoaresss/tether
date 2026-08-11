# Navegador no canvas — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Um terceiro tipo de nó no canvas — um navegador (WebView2) com barra de URL e voltar/avançar/recarregar — para acompanhar previews de localhost e docs ao lado dos agentes.

**Architecture:** `BrowserNode` entra como terceiro `JsonDerivedType` de `NodeBase` (discriminador `"browser"`). `BrowserNodeView` segue o padrão dos outros views: chrome escalado por `Camera.ChromeScale` (header de identidade + linha de navegação, um único `ScaleTransform`), corpo WebView2. O zoom do canvas chega à página como CSS zoom via `ExecuteScriptAsync` — o WebView2 do WinUI 3 não expõe `CoreWebView2Controller.ZoomFactor`, e CSS zoom reaplica-se barato a cada navegação. Ctrl+roda sobre a página é devolvido ao canvas pelo mesmo mecanismo de mensagem que o terminal já usa.

**Tech Stack:** WinUI 3 (Windows App SDK), WebView2, System.Text.Json polimórfico, xUnit.

**Fora do escopo (v1), decidido e registrado:** agentes não dirigem o navegador (nenhum comando `tether` novo; um nó browser aparece no `tether list` rotulado `browser` e os comandos existentes o recusam por checagem de tipo, que já é o comportamento de `ask` contra nota). Sem histórico, favoritos, abas internas ou captura de conteúdo para agentes.

## Global Constraints

- O `dotnet` fica em `%USERPROFILE%\.dotnet\dotnet.exe` (não há Visual Studio). Nos comandos: `$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"`.
- Chrome do nó escala por `Camera.ChromeScale(zoom)` = `min(zoom, 1)` — proporcional abaixo de 1×, travado no padrão acima. O view novo usa exatamente o padrão `SyncHeaderChrome` dos views existentes.
- Colapso abaixo de `Camera.CollapseZoom` (0.15): WebView2 escondido (`Web.Visibility`), header vira o cartão. `PlaceNode` não tem pisos — o nó encolhe honestamente.
- Persistência: um workspace novo lido por build antigo falha no discriminador desconhecido e cai no `.bak` — risco aceito, igual quando as abas entraram (app de usuário único, sem downgrade).
- Todo commit compila (`& $dotnet build`, 0 avisos) e passa a suíte (`& $dotnet test tests/Orchestration.Core.Tests`).
- Commits em inglês, Conventional Commits.
- Strings de UI em pt-BR sem acento nos headers de diálogo já existentes seguem o padrão do arquivo onde entram (o codebase mistura; copie o vizinho).

---

### Task 1: Core — `BrowserNode`, `CompleteUrl` e persistência

**Files:**
- Modify: `src/Orchestration.Core/Models/NodeBase.cs`
- Modify: `src/Orchestration.Core/Persistence/WorkspaceStore.cs` (método `Normalize(CanvasTab)`, ~linha 132)
- Test: `tests/Orchestration.Core.Tests/Models/WorkspaceJsonTests.cs`
- Test: `tests/Orchestration.Core.Tests/Models/BrowserNodeTests.cs` (novo)

**Interfaces:**
- Consumes: nada novo.
- Produces: `BrowserNode : NodeBase` com `string Url` (default `""`) e `static string CompleteUrl(string text)`. Discriminador JSON `"browser"`. Tasks 3 e 4 dependem desses nomes exatos.

- [ ] **Step 1: Testes primeiro — round-trip e CompleteUrl**

Novo arquivo `tests/Orchestration.Core.Tests/Models/BrowserNodeTests.cs`:

```csharp
using Orchestration.Core.Models;
using Xunit;

namespace Orchestration.Core.Tests;

public class BrowserNodeTests
{
    /// <summary>
    /// The address box is where "localhost:3000" gets typed. A bare https:// prefix would break
    /// exactly that primary case — dev servers speak http — so local hosts get http instead.
    /// </summary>
    [Theory]
    [InlineData("localhost:3000", "http://localhost:3000")]
    [InlineData("127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("example.com/docs", "https://example.com/docs")]
    [InlineData("https://claude.ai", "https://claude.ai")]
    [InlineData("http://interno:5000", "http://interno:5000")]
    [InlineData("  example.com  ", "https://example.com")]
    [InlineData("   ", "")]
    public void CompleteUrl_FillsTheSchemeTheUserDidNotType(string typed, string expected)
    {
        Assert.Equal(expected, BrowserNode.CompleteUrl(typed));
    }
}
```

Em `tests/Orchestration.Core.Tests/Models/WorkspaceJsonTests.cs`, dentro de `SampleWorkspace()`, logo após a criação de `note`, adicionar:

```csharp
        var browser = new BrowserNode
        {
            Title = "preview",
            X = 800, Y = 300, Width = 720, Height = 480,
            Url = "http://localhost:3000"
        };
```

e incluir `browser` na lista de nós do tab exatamente onde `terminal` e `note` já entram (a lista `Nodes` do sample — o arquivo mostra a forma; acrescente como terceiro item). No teste de round-trip (o que hoje faz `Assert.IsType<TerminalNode>(Canvas(loaded).Nodes[0])`), adicionar:

```csharp
        var browser = Assert.IsType<BrowserNode>(Canvas(loaded).Nodes[2]);
        Assert.Equal("http://localhost:3000", browser.Url);
```

- [ ] **Step 2: Rodar e ver falhar**

```powershell
$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"; & $dotnet test tests/Orchestration.Core.Tests --filter "FullyQualifiedName~BrowserNode"
```

Expected: falha de compilação — `BrowserNode` não existe.

- [ ] **Step 3: Modelo**

Em `src/Orchestration.Core/Models/NodeBase.cs`, adicionar o derived type ao atributo e a classe no fim do arquivo:

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TerminalNode), "terminal")]
[JsonDerivedType(typeof(NoteNode), "note")]
[JsonDerivedType(typeof(BrowserNode), "browser")]
```

```csharp
public sealed class BrowserNode : NodeBase
{
    /// <summary>Last address navigated to; written back on every navigation so reload restores it.</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// What the user typed in the address box, made navigable. Local hosts get http because dev
    /// servers speak http, and the address box is exactly where "localhost:3000" gets typed;
    /// everything else without a scheme gets https. No search-engine fallback: this is a preview
    /// pane, not a browser product.
    /// </summary>
    public static string CompleteUrl(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Contains("://")) return trimmed;
        bool local = trimmed.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                     trimmed.StartsWith("127.");
        return (local ? "http://" : "https://") + trimmed;
    }
}
```

Em `src/Orchestration.Core/Persistence/WorkspaceStore.cs`, no `Normalize(CanvasTab)`, logo após o `foreach` dos terminais:

```csharp
        // Same repair the other node kinds get: a hand-edited null must not NRE downstream.
        foreach (var browser in tab.Nodes.OfType<BrowserNode>())
            browser.Url ??= "";
```

- [ ] **Step 4: Rodar tudo e ver passar**

```powershell
$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"; & $dotnet test tests/Orchestration.Core.Tests
```

Expected: 100/100 — 93 atuais + 7 casos da theory (os asserts novos no round-trip entram num fato que já existe e não mudam a contagem).

- [ ] **Step 5: Commit**

```powershell
git add src/Orchestration.Core tests/Orchestration.Core.Tests; git commit -m "feat: BrowserNode model with url completion and persistence"
```

---

### Task 2: Ambiente WebView2 compartilhado

**Files:**
- Create: `src/Orchestration.App/Services/TetherWebView.cs`
- Modify: `src/Orchestration.App/Views/TerminalNodeView.xaml.cs` (campos `_sharedEnvironment`/`EnvironmentLock` ~linhas 22–23 e o bloco de criação em `OnLoaded` ~linhas 98–113)

**Interfaces:**
- Consumes: nada.
- Produces: `static Task<CoreWebView2Environment> TetherWebView.SharedEnvironmentAsync()` — Task 3 chama isso.

- [ ] **Step 1: Extrair o helper**

Novo arquivo `src/Orchestration.App/Services/TetherWebView.cs`:

```csharp
using Microsoft.Web.WebView2.Core;

namespace Orchestration.App.Services;

/// <summary>
/// One browser process family for every WebView2 in the app — terminals and browser nodes alike.
/// Two environments pointing at the same user data folder must be created with identical options,
/// which is exactly the kind of agreement that silently rots when each view carries its own copy.
/// </summary>
public static class TetherWebView
{
    private static CoreWebView2Environment? _environment;
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public static async Task<CoreWebView2Environment> SharedEnvironmentAsync()
    {
        await Lock.WaitAsync();
        try
        {
            return _environment ??= await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: null,
                userDataFolder: Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Orchestration", "WebView2"),
                options: new CoreWebView2EnvironmentOptions());
        }
        finally
        {
            Lock.Release();
        }
    }
}
```

Em `TerminalNodeView.xaml.cs`: apagar os campos `_sharedEnvironment` e `EnvironmentLock` (e o comentário "One browser process family…"), e trocar o bloco `await EnvironmentLock.WaitAsync(); … finally { EnvironmentLock.Release(); }` + `await Web.EnsureCoreWebView2Async(_sharedEnvironment);` por:

```csharp
        await Web.EnsureCoreWebView2Async(await Services.TetherWebView.SharedEnvironmentAsync());
```

- [ ] **Step 2: Compilar e rodar a suíte**

```powershell
$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"; & $dotnet build; & $dotnet test tests/Orchestration.Core.Tests
```

Expected: 0 avisos, suíte verde (nada no Core mudou — regressão apenas).

- [ ] **Step 3: Commit**

```powershell
git add src/Orchestration.App; git commit -m "refactor: share one WebView2 environment across node kinds"
```

---

### Task 3: `BrowserNodeView`

**Files:**
- Create: `src/Orchestration.App/Views/BrowserNodeView.xaml`
- Create: `src/Orchestration.App/Views/BrowserNodeView.xaml.cs`

**Interfaces:**
- Consumes: `TetherWebView.SharedEnvironmentAsync()` (Task 2), `Camera.ChromeScale`, `Camera.CollapseZoom` (existentes), `BrowserNode.CompleteUrl` (Task 1).
- Produces: `BrowserNodeView : UserControl, INodeView` com `string Title`, `string Url { get; set; }` (set navega), eventos `Action<BrowserNodeView>? CloseRequested`, `Action<BrowserNodeView>? UrlChanged`, `Action<double>? ZoomRequested`. Task 4 usa esses nomes.

- [ ] **Step 1: XAML**

O esqueleto é o do `NoteNodeView.xaml` (mesmos anéis de seleção/hover, grip, superfície de conexão); o header ganha uma segunda linha — a de navegação — dentro do MESMO `HeaderContent` escalado, então um único transform cobre tudo. `src/Orchestration.App/Views/BrowserNodeView.xaml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<UserControl
    x:Class="Orchestration.App.Views.BrowserNodeView"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:controls="using:Microsoft.UI.Xaml.Controls">

    <Grid x:Name="NodeRoot"
          PointerEntered="OnNodePointerEntered"
          PointerExited="OnNodePointerExited">
        <Border x:Name="NodeBorder"
                Background="{ThemeResource TetherNodeBrush}"
                BorderBrush="{ThemeResource TetherBorderBrush}"
                BorderThickness="1"
                CornerRadius="12">
            <Grid>
                <Grid.RowDefinitions>
                    <RowDefinition x:Name="HeaderRow" Height="76" />
                    <RowDefinition x:Name="ContentRow" Height="*" />
                </Grid.RowDefinitions>

                <Grid x:Name="HeaderBar"
                      Background="{ThemeResource TetherNodeHeaderBrush}"
                      SizeChanged="OnHeaderSizeChanged">
                    <!-- Laid out at 1x, painted scaled. See SyncHeaderChrome. -->
                    <Grid x:Name="HeaderContent"
                          Height="76"
                          Padding="12,0"
                          RenderTransformOrigin="0,0">
                        <Grid.RenderTransform>
                            <ScaleTransform x:Name="HeaderScale" />
                        </Grid.RenderTransform>
                        <Grid.RowDefinitions>
                            <RowDefinition Height="44" />
                            <RowDefinition Height="32" />
                        </Grid.RowDefinitions>

                        <Grid ColumnSpacing="9">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <Border Padding="6,2"
                                    CornerRadius="4"
                                    Background="{StaticResource TetherBadgeBrush}"
                                    VerticalAlignment="Center">
                                <TextBlock Text="NAVEGADOR"
                                           Foreground="White"
                                           FontFamily="Segoe UI Variable Text"
                                           FontSize="9"
                                           FontWeight="Bold"
                                           CharacterSpacing="70" />
                            </Border>
                            <TextBlock x:Name="TitleText"
                                       Grid.Column="1"
                                       Text="navegador"
                                       VerticalAlignment="Center"
                                       Foreground="{ThemeResource TetherTextPrimaryBrush}"
                                       FontFamily="Segoe UI Variable Text"
                                       FontSize="12"
                                       FontWeight="SemiBold"
                                       TextTrimming="CharacterEllipsis" />
                            <StackPanel x:Name="HeaderActions" Grid.Column="2" Orientation="Horizontal" Spacing="2">
                                <Button Style="{StaticResource TetherDangerNodeActionButtonStyle}"
                                        Click="OnClose"
                                        ToolTipService.ToolTip="Remover nó">
                                    <FontIcon Glyph="&#xE74D;" FontSize="12" />
                                </Button>
                            </StackPanel>
                        </Grid>

                        <Grid x:Name="NavRow" Grid.Row="1" ColumnSpacing="4">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>
                            <Button Style="{StaticResource TetherNodeActionButtonStyle}"
                                    Click="OnBack" ToolTipService.ToolTip="Voltar">
                                <FontIcon Glyph="&#xE72B;" FontSize="12" />
                            </Button>
                            <Button Grid.Column="1"
                                    Style="{StaticResource TetherNodeActionButtonStyle}"
                                    Click="OnForward" ToolTipService.ToolTip="Avançar">
                                <FontIcon Glyph="&#xE72A;" FontSize="12" />
                            </Button>
                            <Button Grid.Column="2"
                                    Style="{StaticResource TetherNodeActionButtonStyle}"
                                    Click="OnReload" ToolTipService.ToolTip="Recarregar">
                                <FontIcon Glyph="&#xE72C;" FontSize="12" />
                            </Button>
                            <TextBox x:Name="UrlBox"
                                     Grid.Column="3"
                                     VerticalAlignment="Center"
                                     FontFamily="Cascadia Mono, Consolas"
                                     FontSize="11"
                                     PlaceholderText="localhost:3000 ou https://..."
                                     KeyDown="OnUrlKeyDown" />
                        </Grid>
                    </Grid>
                </Grid>

                <controls:WebView2 x:Name="Web"
                                   Grid.Row="1"
                                   DefaultBackgroundColor="#0D0A13" />
            </Grid>
        </Border>
        <Border x:Name="HoverRing"
                Margin="-2"
                BorderBrush="{StaticResource TetherAccentVioletBrush}"
                BorderThickness="1"
                CornerRadius="14"
                Opacity="0"
                IsHitTestVisible="False" />
        <Border x:Name="SelectionRing"
                Margin="-2"
                BorderBrush="{StaticResource TetherAccentLimeBrush}"
                BorderThickness="2"
                CornerRadius="14"
                Opacity="0"
                IsHitTestVisible="False" />
        <Border x:Name="ResizeGripElement" Width="20" Height="20"
                HorizontalAlignment="Right" VerticalAlignment="Bottom"
                Background="Transparent"
                ToolTipService.ToolTip="Redimensionar" />
        <Border x:Name="ConnectionSurfaceElement"
                Visibility="Collapsed"
                Background="Transparent"
                ToolTipService.ToolTip="Arraste para conectar" />
    </Grid>
</UserControl>
```

- [ ] **Step 2: Code-behind**

`src/Orchestration.App/Views/BrowserNodeView.xaml.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
        await core.AddScriptToExecuteOnCreatedDocumentAsync(WheelForwarder);

        core.WebMessageReceived += OnWebMessage;
        core.SourceChanged += (_, _) =>
        {
            UrlBox.Text = core.Source;
            UrlChanged?.Invoke(this);
        };
        // CSS zoom does not survive a navigation, so every landing reapplies the current scale.
        core.NavigationCompleted += (_, _) => ApplyPageZoom();
        // A popup that opened a real window would live outside the canvas; keep it in the node.
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            core.Navigate(args.Uri);
        };

        if (_url.Length > 0) Navigate(_url);
    }

    private void OnWebMessage(
        Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
    {
        JsonElement message;
        try { message = JsonDocument.Parse(args.TryGetWebMessageAsString()).RootElement; }
        catch (JsonException) { return; }

        if (message.TryGetProperty("t", out var kind) && kind.GetString() == "zoom")
            ZoomRequested?.Invoke(message.GetProperty("d").GetDouble());
    }

    public void Navigate(string text)
    {
        string url = BrowserNode.CompleteUrl(text);
        if (url.Length == 0 || Web.CoreWebView2 is null) return;
        try
        {
            Web.CoreWebView2.Navigate(url);
        }
        catch (ArgumentException)
        {
            // Malformed address: stay on the current page, the box still shows what was typed.
        }
    }

    private void OnUrlKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        Navigate(UrlBox.Text);
        e.Handled = true;
    }

    private void OnBack(object sender, RoutedEventArgs e) { if (Web.CanGoBack) Web.GoBack(); }
    private void OnForward(object sender, RoutedEventArgs e) { if (Web.CanGoForward) Web.GoForward(); }
    private void OnReload(object sender, RoutedEventArgs e) => Web.Reload();

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
    /// device size divided back into world units — collapsed, that is the whole card, which is how
    /// the miniature keeps its label centred.
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
```

- [ ] **Step 3: Compilar**

```powershell
$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"; & $dotnet build
```

Expected: 0 avisos, 0 erros (o view ainda não é referenciado — o compilador de XAML é o teste desta task).

- [ ] **Step 4: Commit**

```powershell
git add src/Orchestration.App/Views; git commit -m "feat: browser node view with url bar and canvas-owned zoom"
```

---

### Task 4: Ligação no MainWindow — criar, materializar, editar, listar

**Files:**
- Modify: `src/Orchestration.App/MainWindow.Nodes.cs` (`Materialize` ~linha 423; criação junto de `CreateNote` ~linha 392)
- Modify: `src/Orchestration.App/MainWindow.Tools.cs` (`PlacementTools` linha 23, rótulo ~linha 111, branch de placement ~linha 384)
- Modify: `src/Orchestration.App/MainWindow.xaml` (toolbar ~linha 65 e ruler ~linha 219)
- Modify: `src/Orchestration.App/MainWindow.Wires.cs` (`EditNode` ~linha 249)
- Modify: `src/Orchestration.App/MainWindow.Agent.cs` (rótulo do `list`, linha 43)
- Modify: `src/Orchestration.App/Services/AgentPrimer.cs` (método `Kind`, linha 51)

**Interfaces:**
- Consumes: `BrowserNode` (Task 1), `BrowserNodeView` com `Url`, `UrlChanged`, `ZoomRequested`, `CloseRequested` (Task 3), `ArmPlacement`/`Materialize`/`ZoomAtNode`/`RemoveNode` (existentes).
- Produces: nada para tasks seguintes.

- [ ] **Step 1: Materialize + criação**

Em `MainWindow.Nodes.cs`, novo case no switch de `Materialize`, antes do `default`:

```csharp
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
```

Junto de `CreateNote` (mesma região do arquivo):

```csharp
    private void CreateBrowser(double x, double y, double width = 720, double height = 480)
    {
        Materialize(new BrowserNode
        {
            Title = "navegador",
            X = x, Y = y, Width = width, Height = height
        });
    }

    private void OnNewBrowser(object sender, RoutedEventArgs e) => ArmPlacement("place-browser");
```

- [ ] **Step 2: Ferramenta de placement**

Em `MainWindow.Tools.cs`:

Linha 23: `private static readonly string[] PlacementTools = { "place-terminal", "place-note", "place-browser" };`

No switch de rótulos (~linha 111), adicionar: `"place-browser" => "Navegador: clique ou arraste",`

No branch de placement (~linha 384), o `if/else` de terminal/nota vira:

```csharp
            if (tool == "place-terminal")
            {
                if (dragged) CreateTerminal(x, y, Math.Max(width, 240), Math.Max(height, 160));
                else CreateTerminal(start.X, start.Y);
            }
            else if (tool == "place-browser")
            {
                if (dragged) CreateBrowser(x, y, Math.Max(width, 240), Math.Max(height, 160));
                else CreateBrowser(start.X, start.Y);
            }
            else
            {
                if (dragged) CreateNote(x, y, Math.Max(width, 160), Math.Max(height, 100));
                else CreateNote(start.X, start.Y);
            }
```

- [ ] **Step 3: Botões**

Em `MainWindow.xaml`, na toolbar após o botão "Nota" (~linha 71):

```xml
                    <Button Style="{StaticResource TetherToolbarButtonStyle}"
                            Click="OnNewBrowser">
                        <StackPanel Orientation="Horizontal" Spacing="7">
                            <FontIcon Glyph="&#xE774;" FontSize="14" />
                            <TextBlock Text="Navegador" />
                        </StackPanel>
                    </Button>
```

No ruler, após o botão de nota (~linha 223):

```xml
                            <Button Style="{StaticResource TetherNodeActionButtonStyle}"
                                    Click="OnNewBrowser"
                                    ToolTipService.ToolTip="Novo navegador">
                                <FontIcon Glyph="&#xE774;" FontSize="14" />
                            </Button>
```

- [ ] **Step 4: EditNode e rótulos do tether**

Em `MainWindow.Wires.cs`, `EditNode`: após o branch `else if (entry.Model is NoteNode note)`, adicionar:

```csharp
        else if (entry.Model is BrowserNode browser)
        {
            details.Header = "URL";
            details.Text = browser.Url;
            panel.Children.Add(details);
        }
```

E na aplicação do resultado, após o branch do `noteModel`:

```csharp
        else if (entry.Model is BrowserNode browserModel)
        {
            browserModel.Url = BrowserNode.CompleteUrl(details.Text);
            ((Views.BrowserNodeView)entry.Node).Url = browserModel.Url;
        }
```

(O título já é aplicado para qualquer nó no fim do método pelo branch existente de `NoteNodeView`; adicionar o browser lá também:)

```csharp
        if (entry.Node is Views.BrowserNodeView bview) bview.Title = entry.Model.Title;
```

Em `MainWindow.Agent.cs` linha 43, o seletor do `list` vira:

```csharp
                        .Select(node => $"{node.Id}  {NodeKindLabel(node)}  {node.Title}")));
```

com o helper (no mesmo arquivo):

```csharp
    private static string NodeKindLabel(NodeBase node) =>
        node switch { TerminalNode => "terminal", BrowserNode => "browser", _ => "note" };
```

Em `Services/AgentPrimer.cs` linha 51:

```csharp
    private static string Kind(NodeBase node) =>
        node switch { TerminalNode => "terminal", BrowserNode => "browser", _ => "note" };
```

- [ ] **Step 5: Compilar e rodar a suíte**

```powershell
$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"; & $dotnet build; & $dotnet test tests/Orchestration.Core.Tests
```

Expected: 0 avisos; suíte verde.

- [ ] **Step 6: Commit**

```powershell
git add src/Orchestration.App; git commit -m "feat: create, edit and persist browser nodes on the canvas"
```

---

### Task 5: Verificação manual

**Files:** nenhum.

- [ ] **Step 1: Roteiro no app**

Rodar `Orchestration.App.exe` e conferir:

1. Botão "Navegador" na toolbar arma o placement; clique cria nó 720×480, arraste cria no retângulo.
2. Digitar `localhost:3000` na barra + Enter navega para `http://localhost:3000` (suba qualquer dev server antes, ou use `example.com` → `https://example.com`).
3. Voltar/avançar/recarregar funcionam; link que abriria popup navega no próprio nó.
4. Ctrl+roda com o ponteiro sobre a página dá zoom no canvas (não na página); o conteúdo da página escala junto com o nó nos dois sentidos, e o chrome segue a regra dos outros nós (padrão acima de 100%, proporcional abaixo).
5. Fechar e reabrir o app: o nó volta na mesma posição com a mesma URL.
6. Abaixo de 15%: vira cartão; acima, volta com a página viva.
7. `tether list` num terminal do mesmo canvas mostra o nó como `browser`; `tether ask` contra ele responde `target not found or ambiguous`.
8. Arrastar pelo header move o nó; arrastar dentro da página NÃO move o nó; redimensionar pelo grip funciona.

- [ ] **Step 2: Nada a commitar** — falhas voltam para a task correspondente.
