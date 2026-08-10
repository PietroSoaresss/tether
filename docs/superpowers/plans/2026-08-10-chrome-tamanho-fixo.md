# Chrome dos nós em tamanho fixo — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A barra de cima (header) e as letras dela, em terminais e notas, ficam num tamanho fixo (44 px, fontes de 9–12 px) em qualquer nível de zoom — hoje crescem junto com o zoom acima de 100%.

**Architecture:** O bug está em `Camera.ChromeScale(zoom) = Math.Max(zoom, 1)`: segura o chrome em 1× só abaixo de 100%; acima, escala junto. Com o chrome sempre 1×, toda a maquinaria de escala (ScaleTransform `HeaderScale`, `SyncHeaderChrome`, handler `SizeChanged`) vira identidade — a correção é **deletar** essa maquinaria e fixar a linha do header em 44 px no XAML. O corpo (fonte do terminal/nota) continua proporcional ao zoom — isso é requisito de correção (contagem de colunas do pseudoconsole), não entra nesta mudança.

**Tech Stack:** WinUI 3 (Windows App SDK), .NET, xUnit.

## Global Constraints

- O `dotnet` fica em `%USERPROFILE%\.dotnet\dotnet.exe` (não há Visual Studio; SDK user-local). Nos comandos abaixo, `$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"`.
- **Não tocar** em `Camera.FontSize` nem `Camera.LabelSize` — o corpo dos nós e os rótulos do canvas têm suas próprias regras, guardadas por testes em `CameraZoomTests`.
- Comportamento do cartão colapsado (zoom < `Camera.CollapseZoom` = 0.4) permanece: header vira linha `Star`, corpo some.
- Ordem das tasks importa: a Task 1 remove todos os usos de `ChromeScale`; só então a Task 2 pode apagá-lo do Core. Cada commit compila e passa os testes.
- Commits em inglês, Conventional Commits (padrão do repo).

---

### Task 1: Views — header fixo em 44 px, deletar a maquinaria de escala

**Files:**
- Modify: `src/Orchestration.App/Views/TerminalNodeView.xaml` (linhas 22–36)
- Modify: `src/Orchestration.App/Views/TerminalNodeView.xaml.cs` (linhas 18–19, 250–288, 297–313)
- Modify: `src/Orchestration.App/Views/NoteNodeView.xaml` (linhas 21–32)
- Modify: `src/Orchestration.App/Views/NoteNodeView.xaml.cs` (linhas 12–16, 76–123)

**Interfaces:**
- Consumes: `Camera.ChromeScale` ainda existe no Core durante esta task (os usos somem aqui; a definição morre na Task 2).
- Produces: `TerminalNodeView`/`NoteNodeView` sem `SyncHeaderChrome`, sem `OnHeaderSizeChanged`, sem `HeaderScale`; `SetCollapsed(bool)` e `ApplyZoom(double)` mantêm as assinaturas públicas de `INodeView`.

- [ ] **Step 1: TerminalNodeView.xaml — remover transform e handler**

Trocar as linhas 22–36 (abertura do `HeaderBar` até o fechamento do `RenderTransform`):

```xml
                <Grid x:Name="HeaderBar"
                      Background="{ThemeResource TetherNodeHeaderBrush}">
                    <Grid x:Name="HeaderContent"
                          Height="44"
                          Padding="12,0"
                          ColumnSpacing="9">
```

Ou seja: some o `SizeChanged="OnHeaderSizeChanged"`, o comentário "Laid out at 1x…", o `RenderTransformOrigin="0,0"` e o bloco `<Grid.RenderTransform>…</Grid.RenderTransform>`. Todo o resto do header (colunas, badge, título, botões) fica como está.

- [ ] **Step 2: TerminalNodeView.xaml.cs — deletar SyncHeaderChrome e fixar o header**

Doc do `HeaderHeight` (linha 18):

```csharp
    /// <summary>Header height in device pixels — fixed at every zoom.</summary>
    private const double HeaderHeight = 44;
```

`ApplyZoom` (linhas 250–267) perde a chamada de chrome; o doc encolhe para o que sobrou:

```csharp
    /// <summary>
    /// Zoom reaches the body as layout, never as a transform: the WebView2 surface is not scalable.
    /// The font tracks the same scale through <see cref="Camera.FontSize"/>, which is what keeps the
    /// pseudoconsole's column count constant across the zoom range — see the note there.
    /// The header does not participate: chrome is identity, fixed at device size in XAML.
    /// </summary>
    public void ApplyZoom(double zoom)
    {
        _lastZoom = zoom;
        if (Web.CoreWebView2 is null || !_pageReady) return;
        Web.CoreWebView2.PostWebMessageAsString(
            JsonSerializer.Serialize(new
            {
                t = "font",
                size = Camera.FontSize(_baseFontSize, zoom),
                family = _fontFamily
            }));
    }
```

Deletar inteiros: `OnHeaderSizeChanged` (linha 269) e `SyncHeaderChrome` com seu doc (linhas 271–288).

`SetCollapsed` (linhas 297–313): o ramo não-colapsado vira 44 fixo e a chamada final some:

```csharp
    public void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        _collapsed = collapsed;

        // The header grows to fill the node instead of a separate card, so it stays the drag handle
        // the canvas registered at creation time.
        HeaderRow.Height = collapsed
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(HeaderHeight);
        ContentRow.Height = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        HeaderActions.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ProjectText.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        // A zero-height WebView2 still composites; hiding it is what actually buys the frame time.
        Web.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
    }
```

`_lastZoom` fica: `OnWebMessage("ready")` e `ApplySettings` fazem replay de `ApplyZoom(_lastZoom)`.

- [ ] **Step 3: NoteNodeView.xaml — mesma limpeza**

Trocar as linhas 21–32 (abertura do `HeaderBar` até o fechamento do `RenderTransform`):

```xml
                <Grid x:Name="HeaderBar"
                      Background="{ThemeResource TetherNodeHeaderBrush}">
                    <Grid x:Name="HeaderContent"
                          Height="44"
                          Padding="12,0"
                          ColumnSpacing="9">
```

- [ ] **Step 4: NoteNodeView.xaml.cs — mesma deleção**

Doc do `HeaderHeight` (linha 12) igual ao do terminal:

```csharp
    /// <summary>Header height in device pixels — fixed at every zoom.</summary>
    private const double HeaderHeight = 44;
```

Deletar o campo `_lastZoom` (linha 16) — com o chrome fixo ele fica write-only na nota (o terminal mantém o dele, que tem replay).

`ApplyZoom` (linhas 76–90) sem a chamada de chrome nem o `_lastZoom`:

```csharp
    /// <summary>
    /// Text tracks the scale exactly. The old <c>Clamp(…, 12, 48)</c> froze it below zoom 0.92 and
    /// above 3.7, which is what made the note stop matching its own box; staying readable when the
    /// node is tiny is the collapsed card's job, not a clamp's. The header does not participate:
    /// chrome is identity, fixed at device size in XAML.
    /// </summary>
    public void ApplyZoom(double zoom)
    {
        double size = Camera.FontSize(_baseFontSize, zoom);
        Editor.FontSize = size;
        Preview.FontSize = size;
        Editor.Padding = new Thickness(14 * zoom, 12 * zoom, 14 * zoom, 12 * zoom);
        PreviewScroller.Padding = Editor.Padding;
    }
```

`SetCollapsed` (linhas 92–104):

```csharp
    public void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        _collapsed = collapsed;

        // Header grows to fill the node so it stays the drag handle the canvas already hooked.
        HeaderRow.Height = collapsed
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(HeaderHeight);
        ContentRow.Height = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        HeaderActions.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
    }
```

Deletar inteiros: `OnHeaderSizeChanged` (linha 106) e `SyncHeaderChrome` com seu doc (linhas 108–123).

- [ ] **Step 5: Compilar e rodar os testes existentes**

```powershell
$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"; & $dotnet build; & $dotnet test tests/Orchestration.Core.Tests
```

Expected: build sem warnings novos, todos os testes PASS (nenhum teste do Core cobre as views; `ChromeNeverPaintsSmallerThanDeviceSize` ainda passa porque `Camera.ChromeScale` ainda existe).

Conferir que não sobrou uso órfão:

```powershell
Select-String -Path src\**\*.cs, src\**\*.xaml -Pattern 'SyncHeaderChrome|HeaderScale|OnHeaderSizeChanged'
```

Expected: nenhuma linha.

- [ ] **Step 6: Commit**

```powershell
git add src/Orchestration.App/Views; git commit -m "fix: keep node chrome at device size at every zoom"
```

---

### Task 2: Core — apagar `Camera.ChromeScale` e seu teste

**Files:**
- Modify: `src/Orchestration.Core/Models/Camera.cs:36-43`
- Test: `tests/Orchestration.Core.Tests/Models/CameraZoomTests.cs:34-45`

**Interfaces:**
- Consumes: nada — a Task 1 já removeu todos os call sites (`grep ChromeScale` só encontra Core e teste).
- Produces: `Camera` sem `ChromeScale`. `FontSize`, `LabelSize`, constantes e o resto dos testes intactos.

- [ ] **Step 1: Deletar o teste do método que vai morrer**

Em `CameraZoomTests.cs`, remover o fato `ChromeNeverPaintsSmallerThanDeviceSize` inteiro com seu doc (linhas 34–45). Nada o substitui: o invariante novo — header 44 px constante — é um literal no XAML, sem lógica para testar.

- [ ] **Step 2: Deletar o método**

Em `Camera.cs`, remover `ChromeScale` com seu doc (linhas 36–43):

```csharp
    public static double ChromeScale(double zoom) => Math.Max(zoom, 1);
```

e o bloco `/// <summary>` acima dele. `FontSize`, `MinLabelSize`, `LabelSize` e as constantes ficam.

- [ ] **Step 3: Compilar tudo e rodar os testes**

```powershell
$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"; & $dotnet build; & $dotnet test tests/Orchestration.Core.Tests
```

Expected: build limpo (se `ChromeScale` ainda tiver uso em algum lugar, é aqui que quebra), testes PASS.

- [ ] **Step 4: Commit**

```powershell
git add src/Orchestration.Core/Models/Camera.cs tests/Orchestration.Core.Tests/Models/CameraZoomTests.cs; git commit -m "refactor: drop Camera.ChromeScale, chrome no longer scales"
```

---

### Task 3: Verificação manual no app

**Files:** nenhum — só execução.

- [ ] **Step 1: Rodar o app e varrer o zoom**

Abrir a solução e rodar o projeto `Orchestration.App` (F5 ou `& $dotnet run --project src/Orchestration.App`). Num canvas com um terminal e uma nota:

1. Ctrl+roda do mouse do 10% ao 400%: a barra de cima fica sempre com 44 px e as letras (título, badge, botões) sempre do mesmo tamanho; só o corpo cresce/encolhe.
2. Ctrl+roda com o ponteiro **em cima do terminal** (o gesto atravessa o WebView2 via `ZoomRequested`): mesmo resultado.
3. Abaixo de 40%: o nó vira cartão colapsado legível, como antes.
4. Redimensionar um nó pelo grip: o header acompanha a largura, altura segue 44 px.
5. Texto do corpo do terminal continua proporcional (sem reflow de colunas ao cruzar 100%).

Expected: nenhum salto de tamanho no header ao cruzar 100%; sem texto borrado (o ScaleTransform que borrava já não existe).

- [ ] **Step 2: Nada a commitar** — se algo falhar, voltar à task correspondente.
