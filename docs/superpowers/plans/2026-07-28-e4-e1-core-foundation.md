# Fase 1 — Núcleo testável (E4 + E1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Entregar o núcleo headless da Fase 1 — o pipeline de texto que faz `tether ask` devolver transcript legível, e os modelos + persistência que destravam todas as etapas de UI.

**Architecture:** Tudo neste plano vive em `src/Orchestration.Core` (net8.0, zero dependência de UI) exceto a Parte C, que liga a persistência à `MainWindow`. O pipeline de texto é uma cadeia de três peças pequenas e independentes — `AnsiFilter` (corta sequências VT, streaming), `TurnCollapser` (desfaz redraw e duplicata), `IdleDetector` (decide quando o turno acabou). A persistência é `System.Text.Json` com escrita atômica e recuperação por `.bak`.

**Tech Stack:** .NET 8, C#, xunit 2.5.3, `System.Text.Json` com polimorfismo nativo, `TimeProvider` (BCL do .NET 8) + `FakeTimeProvider` para testes de tempo sem `Thread.Sleep`.

**Spec:** `docs/superpowers/specs/2026-07-28-fase1-mvp-design.md`

## Global Constraints

- Alvo: `net8.0` no Core e nos testes; `net8.0-windows10.0.19041.0` no App. `Nullable` e `ImplicitUsings` habilitados em todos os projetos — já estão.
- Comentários em código: **inglês**, seguindo o código existente (`ConPtySession.cs`, `MainWindow.xaml.cs`). Prosa de documentação e mensagens de UI: **português**. Comentário só quando explica *por quê*, nunca *o quê*.
- Namespaces: `Orchestration.Core.Models`, `Orchestration.Core.Terminal`, `Orchestration.Core.Persistence`. Testes ficam todos no namespace plano `Orchestration.Core.Tests`, como o `ConPtySessionTests.cs` existente, mesmo em subpastas.
- **Não regredir o `STARTF_USESTDHANDLES`** em `ConPtySession.Start`. Nenhuma task deste plano toca esse arquivo.
- Zoom nunca vira `ScaleTransform` — WebView2 não é composto pelo XAML. Continua sendo posição/tamanho × zoom.
- Rodar os testes sem tocar no projeto WinUI: `dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj`. Compilar a solução inteira exige o Windows App SDK; os testes não.
- Um commit por task, mensagem em Conventional Commits, em inglês, contendo só os arquivos daquela task.

> **Toolchain — lido em 28/07/2026, antes de executar.** Esta máquina **não tem .NET SDK**: nada no PATH, nada em `%USERPROFILE%\.dotnet` nem em `C:\Program Files\dotnet`, `DOTNET_ROOT` vazio. A afirmação do `PLANO.md` de que o SDK 8.0.423 está em `%USERPROFILE%\.dotnet` descreve **outra máquina** (`C:\Users\pietr\dev\orchestration`) e não vale para este checkout.
>
> Decisão do Pietro em 28/07/2026: executar o plano mesmo assim, transcrevendo o código sem rodar nada, e validar depois de instalar o SDK. Consequência a assumir: **nenhuma task deste plano tem evidência RED/GREEN**, e os passos de verificação (`dotnet test`, `dotnet build`, `dotnet run`, a validação manual contra o `codex` na Task 3) ficaram **pendentes**, não cumpridos. Antes de confiar em qualquer parte disto, instalar o SDK 8 e rodar `dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj` de ponta a ponta.

**Antes da Task 1**, criar a branch de trabalho:

```bash
git checkout -b feat/core-foundation
```

## Estrutura de arquivos

| Arquivo | Responsabilidade |
|---|---|
| `src/Orchestration.Core/Terminal/AnsiFilter.cs` | Máquina de estados streaming: tira VT/C0, rastreia alt-screen |
| `src/Orchestration.Core/Terminal/TurnCollapser.cs` | Texto filtrado → transcript: sobrescrita por `\r`, dedupe de linha, teto de tamanho |
| `src/Orchestration.Core/Terminal/IdleDetector.cs` | Compõe os dois acima + quiescência + timeout duro; entrega o turno |
| `src/Orchestration.Core/Models/NodeBase.cs` | `NodeBase`, `TerminalNode`, `NoteNode`, `NoteViewMode` — conjunto polimórfico fechado, muda junto |
| `src/Orchestration.Core/Models/Connection.cs` | Cabo (autorização) |
| `src/Orchestration.Core/Models/Camera.cs` | Estado de pan/zoom |
| `src/Orchestration.Core/Models/Workspace.cs` | Raiz persistida + `CurrentVersion` |
| `src/Orchestration.Core/Models/AppSettings.cs` | Configurações, arquivo próprio |
| `src/Orchestration.Core/Persistence/TetherJson.cs` | Um único `JsonSerializerOptions` compartilhado |
| `src/Orchestration.Core/Persistence/AtomicFile.cs` | Troca atômica com `.bak` + política de recuperação |
| `src/Orchestration.Core/Persistence/TetherPaths.cs` | Resolve `%AppData%\Tether`; raiz injetável para teste |
| `src/Orchestration.Core/Persistence/WorkspaceStore.cs` | Load/Save/migração do workspace |
| `src/Orchestration.Core/Persistence/SettingsStore.cs` | Load/Save das configurações |
| `src/Orchestration.Core/Persistence/Autosave.cs` | Debounce de escrita |
| `src/Orchestration.App/MainWindow.Canvas.cs` | (split) pan, zoom, `PlaceNode`, `ApplyLayout` |
| `src/Orchestration.App/MainWindow.Nodes.cs` | (split) criar, remover, arrastar nós |
| `src/Orchestration.App/MainWindow.xaml.cs` | (fica) ctor, toolbar, carga/salvamento, teardown |

**Refinamento em relação ao spec §11:** o spec descreve colapso de redraw como responsabilidade do `AnsiFilter`. Aqui isso sai para `TurnCollapser`. Mesmo comportamento, duas peças testáveis em separado — "tirar escape" e "montar transcript" falham por motivos diferentes e merecem testes diferentes.

## Ordem e independência

- **Parte A (Tasks 1–3)** = etapa E4 do spec. Zero dependências. Vem primeiro porque é o único trecho com risco técnico novo — se o colapso de redraw não servir para o `codex`, é melhor descobrir agora que depois da CLI pronta.
- **Parte B (Tasks 4–8)** = etapa E1 do spec, lado Core. Independente da Parte A; as duas podem trocar de ordem ou rodar em paralelo.
- **Parte C (Tasks 9–10)** = etapa E1, lado App. Depende da Parte B.

---

## Parte A — Pipeline de texto (E4)

### Task 1: AnsiFilter

**Files:**
- Create: `src/Orchestration.Core/Terminal/AnsiFilter.cs`
- Test: `tests/Orchestration.Core.Tests/Terminal/AnsiFilterTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces: `Orchestration.Core.Terminal.AnsiFilter` com `string Feed(ReadOnlySpan<byte> chunk)` e `bool InAltScreen { get; }`. Instância é stateful e **não** é thread-safe: uma por stream.

O ponto inteiro desta classe é sobreviver a fronteiras de chunk. O `ConPtySession` lê em blocos de 4 KB, então sequência de escape partida e codepoint UTF-8 partido são o caso comum, não a exceção. Por isso o teste principal é exaustivo sobre todos os pontos de corte possíveis.

- [ ] **Step 1: Write the failing test**

Criar `tests/Orchestration.Core.Tests/Terminal/AnsiFilterTests.cs`:

```csharp
using System.Text;
using Orchestration.Core.Terminal;
using Xunit;

namespace Orchestration.Core.Tests;

public class AnsiFilterTests
{
    // A little of everything the real thing emits: erase/home, an OSC title, SGR colour,
    // the alt-screen toggle, a multi-byte codepoint and a wide one.
    private const string Rich =
        "\x1b[2J\x1b[H\x1b]0;pwsh\aPS \x1b[32mC:\\dev\x1b[0m> echo oi\r\n" +
        "oi\r\n\x1b[?1049h\x1b[?1049lcaf\u00e9 \u2713\r\n";

    private static byte[] RichBytes() => Encoding.UTF8.GetBytes(Rich);

    [Fact]
    public void Feed_StripsCsiSequences()
    {
        var filter = new AnsiFilter();
        Assert.Equal("red", filter.Feed(Encoding.UTF8.GetBytes("\x1b[31mred\x1b[0m")));
    }

    [Fact]
    public void Feed_KeepsCarriageReturnLineFeedAndTab()
    {
        var filter = new AnsiFilter();
        Assert.Equal("a\r\n\tb", filter.Feed(Encoding.UTF8.GetBytes("a\r\n\tb")));
    }

    [Fact]
    public void Feed_DropsOtherControlCharacters()
    {
        var filter = new AnsiFilter();
        Assert.Equal("ab", filter.Feed(Encoding.UTF8.GetBytes("\x07a\x00b")));
    }

    [Fact]
    public void Feed_StripsOscTerminatedByBel()
    {
        var filter = new AnsiFilter();
        Assert.Equal("X", filter.Feed(Encoding.UTF8.GetBytes("\x1b]0;titulo\aX")));
    }

    [Fact]
    public void Feed_StripsOscTerminatedByStringTerminator()
    {
        var filter = new AnsiFilter();
        Assert.Equal("X", filter.Feed(Encoding.UTF8.GetBytes("\x1b]0;titulo\x1b\\X")));
    }

    [Fact]
    public void Feed_TracksAlternateScreenBuffer()
    {
        var filter = new AnsiFilter();
        Assert.False(filter.InAltScreen);

        filter.Feed(Encoding.UTF8.GetBytes("\x1b[?1049h"));
        Assert.True(filter.InAltScreen);

        filter.Feed(Encoding.UTF8.GetBytes("\x1b[?1049l"));
        Assert.False(filter.InAltScreen);
    }

    /// <summary>
    /// The guarantee that matters: 4 KB reads cut sequences and codepoints in half all day,
    /// so the result must not depend on where the cut lands.
    /// </summary>
    [Fact]
    public void Feed_IsIndependentOfChunkBoundaries()
    {
        byte[] bytes = RichBytes();
        string whole = new AnsiFilter().Feed(bytes);

        for (int split = 1; split < bytes.Length; split++)
        {
            var filter = new AnsiFilter();
            string first = filter.Feed(bytes.AsSpan(0, split));
            string second = filter.Feed(bytes.AsSpan(split));
            Assert.Equal(whole, first + second);
        }
    }

    [Fact]
    public void Feed_IsIndependentOfRandomMultiWaySplits()
    {
        byte[] bytes = RichBytes();
        string whole = new AnsiFilter().Feed(bytes);
        var random = new Random(20260728);

        for (int attempt = 0; attempt < 200; attempt++)
        {
            var filter = new AnsiFilter();
            var rebuilt = new StringBuilder();
            int offset = 0;
            while (offset < bytes.Length)
            {
                int take = random.Next(1, 7);
                take = Math.Min(take, bytes.Length - offset);
                rebuilt.Append(filter.Feed(bytes.AsSpan(offset, take)));
                offset += take;
            }
            Assert.Equal(whole, rebuilt.ToString());
        }
    }

    [Fact]
    public void Feed_HandlesCodepointSplitAcrossChunks()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("caf\u00e9");
        var filter = new AnsiFilter();

        // "é" is two bytes; cut between them.
        string first = filter.Feed(bytes.AsSpan(0, bytes.Length - 1));
        string second = filter.Feed(bytes.AsSpan(bytes.Length - 1));

        Assert.Equal("caf\u00e9", first + second);
    }

    [Fact]
    public void Feed_RecoversFromAnAbsurdlyLongCsi()
    {
        var filter = new AnsiFilter();
        // The sequence must still be consumed to its final byte; bailing out early would
        // spill the leftover parameter bytes into the output as text.
        string garbage = "\x1b[" + new string('0', 200) + "m" + "ok";
        Assert.Equal("ok", filter.Feed(Encoding.UTF8.GetBytes(garbage)));
    }

    [Fact]
    public void Feed_StripsCharsetDesignationEscapes()
    {
        var filter = new AnsiFilter();
        // ESC ( 0 selects DEC special graphics for box drawing, ESC ( B returns to ASCII.
        // These are three bytes, not two, so the final byte must not reach the output.
        Assert.Equal("ok", filter.Feed(Encoding.UTF8.GetBytes("\x1b(0\x1b(Bok")));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj --filter "FullyQualifiedName~AnsiFilterTests"
```

Esperado: FALHA na compilação — `The type or namespace name 'AnsiFilter' could not be found`.

- [ ] **Step 3: Write minimal implementation**

Criar `src/Orchestration.Core/Terminal/AnsiFilter.cs`:

```csharp
using System.Text;

namespace Orchestration.Core.Terminal;

/// <summary>
/// Streaming VT sequence stripper. The pseudoconsole hands us 4 KB reads, which cut escape
/// sequences and UTF-8 codepoints in half constantly, so parser state has to survive between
/// calls. A per-chunk regex cannot do this and would silently leak escape bytes downstream.
/// One instance per stream: stateful, not thread-safe.
/// </summary>
public sealed class AnsiFilter
{
    private enum State { Ground, Escape, EscapeIntermediate, Csi, StringSeq, StringSeqEscape }

    // A CSI longer than this is malformed; stop buffering rather than grow forever.
    private const int MaxCsiLength = 64;

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _csi = new();
    private State _state = State.Ground;
    private char[] _chars = new char[1024];

    /// <summary>True while the child is painting on the alternate screen buffer (ESC[?1049h).</summary>
    public bool InAltScreen { get; private set; }

    public string Feed(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty) return string.Empty;

        // Decode first: every escape byte is ASCII, and UTF-8 continuation bytes are >= 0x80,
        // so decoding can never turn payload into a false escape.
        int needed = _decoder.GetCharCount(chunk, flush: false);
        if (_chars.Length < needed) _chars = new char[needed];
        int count = _decoder.GetChars(chunk, _chars, flush: false);

        var output = new StringBuilder(count);
        for (int i = 0; i < count; i++) Step(_chars[i], output);
        return output.ToString();
    }

    private void Step(char c, StringBuilder output)
    {
        switch (_state)
        {
            case State.Ground:
                if (c == '\x1b') _state = State.Escape;
                else if (c == '\x9b') { _csi.Clear(); _state = State.Csi; }
                else if (c == '\x7f') { }
                else if (c < ' ' && c != '\r' && c != '\n' && c != '\t') { }
                else output.Append(c);
                break;

            case State.Escape:
                if (c == '[') { _csi.Clear(); _state = State.Csi; }
                // OSC, DCS, PM, APC and SOS all run until a string terminator.
                else if (c is ']' or 'P' or '^' or '_' or 'X') _state = State.StringSeq;
                // An intermediate byte means a longer form such as the charset designation
                // ESC ( 0, which curses-style TUIs emit for box drawing. Treating it as a
                // two-character escape would spill its final byte into the output.
                else if (c >= '\x20' && c <= '\x2f') _state = State.EscapeIntermediate;
                // Everything else really is two characters (ESC 7, ESC =, ESC c ...).
                else _state = State.Ground;
                break;

            case State.EscapeIntermediate:
                // Intermediates may repeat; anything outside their range is the final byte.
                if (c < '\x20' || c > '\x2f') _state = State.Ground;
                break;

            case State.Csi:
                // Parameter and intermediate bytes are 0x20-0x3F, the final byte is 0x40-0x7E.
                if (c >= '\x40' && c <= '\x7e') { FinishCsi(c); _state = State.Ground; }
                // Stop buffering an overlong, malformed CSI but keep consuming it. Returning
                // to Ground here would emit the rest of the sequence as literal text, which is
                // the exact opposite of this class's job.
                else if (_csi.Length < MaxCsiLength) _csi.Append(c);
                break;

            case State.StringSeq:
                if (c == '\a') _state = State.Ground;
                else if (c == '\x1b') _state = State.StringSeqEscape;
                break;

            case State.StringSeqEscape:
                _state = c == '\\' ? State.Ground : State.StringSeq;
                break;
        }
    }

    private void FinishCsi(char final)
    {
        // Only one CSI changes anything downstream: the alternate screen toggle. A fullscreen
        // TUI repaints a whole grid every frame, which is noise rather than transcript.
        if (final is not ('h' or 'l')) return;

        string parameters = _csi.ToString();
        if (parameters is "?1049" or "?1047" or "?47") InAltScreen = final == 'h';
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj --filter "FullyQualifiedName~AnsiFilterTests"
```

Esperado: PASS, 11 testes.

- [ ] **Step 5: Commit**

```bash
git add src/Orchestration.Core/Terminal/AnsiFilter.cs tests/Orchestration.Core.Tests/Terminal/AnsiFilterTests.cs
git commit -m "feat(core): add streaming ANSI filter with alt-screen tracking"
```

---

### Task 2: TurnCollapser

**Files:**
- Create: `src/Orchestration.Core/Terminal/TurnCollapser.cs`
- Test: `tests/Orchestration.Core.Tests/Terminal/TurnCollapserTests.cs`

**Interfaces:**
- Consumes: nada (recebe texto já filtrado, como `string`).
- Produces: `Orchestration.Core.Terminal.TurnCollapser` com construtor `TurnCollapser(int capChars = TurnCollapser.DefaultCapChars)`, `void Append(string text)`, `string Result { get; }`, `void Reset()`, e a constante `DefaultCapChars`.

Por que existe: tirar ANSI de uma TUI que se redesenha **não** produz transcript limpo, produz texto duplicado. O spike F0 capturou o PSReadLine repintando a mesma linha (`echo P` → `echo PR` → `echo PROVA_CONP`, com `ESC[1;38H` entre cada). Sem esta peça, `tether ask` devolve lixo.

- [ ] **Step 1: Write the failing test**

Criar `tests/Orchestration.Core.Tests/Terminal/TurnCollapserTests.cs`:

```csharp
using Orchestration.Core.Terminal;
using Xunit;

namespace Orchestration.Core.Tests;

public class TurnCollapserTests
{
    [Fact]
    public void Append_CarriageReturnOverwritesTheCurrentLine()
    {
        var collapser = new TurnCollapser();
        collapser.Append("echo P\recho PR\recho PROVA\n");
        Assert.Equal("echo PROVA", collapser.Result);
    }

    [Fact]
    public void Append_OverwriteShorterThanTheLine_KeepsTheTail()
    {
        var collapser = new TurnCollapser();
        // CR homes the cursor; "xy" overwrites two cells and "cdef" survives untouched.
        collapser.Append("abcdef\rxy");
        Assert.Equal("xycdef", collapser.Result);
    }

    [Fact]
    public void Append_DropsConsecutiveDuplicateLines()
    {
        var collapser = new TurnCollapser();
        collapser.Append("a\na\na\nb\n");
        Assert.Equal("a\nb", collapser.Result);
    }

    [Fact]
    public void Append_KeepsDuplicatesThatAreNotAdjacent()
    {
        var collapser = new TurnCollapser();
        collapser.Append("a\nb\na\n");
        Assert.Equal("a\nb\na", collapser.Result);
    }

    [Fact]
    public void Result_IncludesTheUnterminatedTail()
    {
        var collapser = new TurnCollapser();
        collapser.Append("linha\nparcial");
        Assert.Equal("linha\nparcial", collapser.Result);
    }

    [Fact]
    public void Append_AcrossCallsBehavesLikeOneCall()
    {
        var split = new TurnCollapser();
        split.Append("echo P\recho ");
        split.Append("PROVA\nfim\n");

        var whole = new TurnCollapser();
        whole.Append("echo P\recho PROVA\nfim\n");

        Assert.Equal(whole.Result, split.Result);
    }

    [Fact]
    public void Append_TrimsFromTheFrontWhenOverCap()
    {
        var collapser = new TurnCollapser(capChars: 32);
        for (int i = 0; i < 50; i++) collapser.Append($"linha-{i}\n");

        string result = collapser.Result;
        Assert.True(result.Length <= 32, $"esperado <= 32, veio {result.Length}");
        Assert.DoesNotContain("linha-0\n", result);
        Assert.Contains("linha-49", result);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var collapser = new TurnCollapser();
        collapser.Append("a\nb\n");
        collapser.Reset();
        Assert.Equal("", collapser.Result);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj --filter "FullyQualifiedName~TurnCollapserTests"
```

Esperado: FALHA na compilação — `The type or namespace name 'TurnCollapser' could not be found`.

- [ ] **Step 3: Write minimal implementation**

Criar `src/Orchestration.Core/Terminal/TurnCollapser.cs`:

```csharp
using System.Text;

namespace Orchestration.Core.Terminal;

/// <summary>
/// Turns filtered terminal text into a transcript.
/// Agent CLIs repaint: the F0 spike caught PSReadLine redrawing the same row as "echo P",
/// then "echo PR", then "echo PROVA_CONP". Concatenating those verbatim produces duplicated
/// text, not a transcript, so carriage-return overwrite and adjacent-duplicate collapse are
/// load-bearing, not polish.
/// </summary>
public sealed class TurnCollapser
{
    public const int DefaultCapChars = 256 * 1024;

    private readonly List<string> _lines = new();
    private readonly StringBuilder _current = new();
    private readonly int _cap;
    private int _column;
    private int _length;

    public TurnCollapser(int capChars = DefaultCapChars) => _cap = capChars;

    public void Append(string text)
    {
        foreach (char c in text)
        {
            switch (c)
            {
                case '\r':
                    _column = 0;
                    break;

                case '\n':
                    CommitLine();
                    break;

                default:
                    if (_column < _current.Length) _current[_column] = c;
                    else _current.Append(c);
                    _column++;
                    break;
            }
        }
    }

    public string Result
    {
        get
        {
            if (_current.Length == 0) return string.Join('\n', _lines);
            if (_lines.Count == 0) return _current.ToString();
            return string.Join('\n', _lines) + "\n" + _current;
        }
    }

    public void Reset()
    {
        _lines.Clear();
        _current.Clear();
        _column = 0;
        _length = 0;
    }

    private void CommitLine()
    {
        string line = _current.ToString();
        _current.Clear();
        _column = 0;

        // The same row painted twice in a row is a redraw, not new output.
        if (_lines.Count > 0 && _lines[^1] == line) return;

        _lines.Add(line);
        _length += line.Length + 1;

        // A source that never quiesces (think `yes`) must not grow without bound.
        while (_length > _cap && _lines.Count > 1)
        {
            _length -= _lines[0].Length + 1;
            _lines.RemoveAt(0);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj --filter "FullyQualifiedName~TurnCollapserTests"
```

Esperado: PASS, 8 testes.

- [ ] **Step 5: Commit**

```bash
git add src/Orchestration.Core/Terminal/TurnCollapser.cs tests/Orchestration.Core.Tests/Terminal/TurnCollapserTests.cs
git commit -m "feat(core): collapse terminal redraws into a transcript"
```

---

### Task 3: IdleDetector

**Files:**
- Create: `src/Orchestration.Core/Terminal/IdleDetector.cs`
- Modify: `tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj` (adiciona `Microsoft.Extensions.TimeProvider.Testing`)
- Test: `tests/Orchestration.Core.Tests/Terminal/IdleDetectorTests.cs`

**Interfaces:**
- Consumes: `AnsiFilter` (Task 1) e `TurnCollapser` (Task 2).
- Produces:
  - `enum Orchestration.Core.Terminal.TurnOutcome { Idle, Timeout, TargetExited }`
  - `sealed record Orchestration.Core.Terminal.TurnResult(string Text, TurnOutcome Outcome)`
  - `sealed class Orchestration.Core.Terminal.IdleDetector : IDisposable` com construtor `IdleDetector(TimeSpan idle, TimeSpan timeout, TimeProvider? time = null)`, `void Push(ReadOnlySpan<byte> chunk)`, `void Complete(TurnOutcome outcome)`, `Task<TurnResult> Completion { get; }`, `bool InAltScreen { get; }`.

O `TetherServer` (etapa E5) vai instanciar um por `ask`, ligar `Push` ao `OutputProduced` do nó alvo e aguardar `Completion`.

Testes de tempo usam `FakeTimeProvider` — nada de `Thread.Sleep`, que produz teste lento e intermitente.

- [ ] **Step 1: Add the test-only time package**

Editar `tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj`, dentro do primeiro `<ItemGroup>` de `PackageReference`, logo depois da linha do `coverlet.collector`:

```xml
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="8.10.0" />
```

Conferir que restaura:

```bash
dotnet restore tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj
```

Esperado: `Restored ...`, sem erro. Se a versão não existir no feed, rodar `dotnet package search Microsoft.Extensions.TimeProvider.Testing` e usar a mais nova da linha 8.x.

- [ ] **Step 2: Write the failing test**

Criar `tests/Orchestration.Core.Tests/Terminal/IdleDetectorTests.cs`:

```csharp
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Orchestration.Core.Terminal;
using Xunit;

namespace Orchestration.Core.Tests;

public class IdleDetectorTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task Completion_ResolvesOnceOutputGoesQuiet()
    {
        var time = new FakeTimeProvider();
        using var detector = new IdleDetector(Idle, Timeout, time);

        detector.Push(Utf8("resposta\n"));
        Assert.False(detector.Completion.IsCompleted);

        time.Advance(Idle);

        var result = await detector.Completion;
        Assert.Equal(TurnOutcome.Idle, result.Outcome);
        Assert.Equal("resposta", result.Text);
    }

    [Fact]
    public void Push_ResetsTheIdleWindow()
    {
        var time = new FakeTimeProvider();
        using var detector = new IdleDetector(Idle, Timeout, time);

        detector.Push(Utf8("a\n"));
        time.Advance(TimeSpan.FromMilliseconds(1400));
        detector.Push(Utf8("b\n"));
        time.Advance(TimeSpan.FromMilliseconds(1400));

        Assert.False(detector.Completion.IsCompleted);
    }

    [Fact]
    public async Task Completion_HitsTheHardTimeout_WhenOutputNeverStops()
    {
        var time = new FakeTimeProvider();
        using var detector = new IdleDetector(Idle, Timeout, time);

        // A source that keeps chattering would reset the idle window forever.
        for (int i = 0; i < 200; i++)
        {
            detector.Push(Utf8($"tick {i}\n"));
            time.Advance(TimeSpan.FromMilliseconds(1000));
        }

        var result = await detector.Completion;
        Assert.Equal(TurnOutcome.Timeout, result.Outcome);
        Assert.Contains("tick", result.Text);
    }

    [Fact]
    public async Task Complete_HandsBackWhatArrivedSoFar()
    {
        var time = new FakeTimeProvider();
        using var detector = new IdleDetector(Idle, Timeout, time);

        detector.Push(Utf8("meia resp"));
        detector.Complete(TurnOutcome.TargetExited);

        var result = await detector.Completion;
        Assert.Equal(TurnOutcome.TargetExited, result.Outcome);
        Assert.Equal("meia resp", result.Text);
    }

    [Fact]
    public async Task Text_IsFilteredAndCollapsed()
    {
        var time = new FakeTimeProvider();
        using var detector = new IdleDetector(Idle, Timeout, time);

        detector.Push(Utf8("\x1b[32mecho P\r\x1b[32mecho PROVA\r\n"));
        detector.Push(Utf8("\x1b]0;titulo\aresposta\r\n"));
        time.Advance(Idle);

        var result = await detector.Completion;
        Assert.Equal("echo PROVA\nresposta", result.Text);
    }

    [Fact]
    public async Task Push_AfterCompletion_IsIgnored()
    {
        var time = new FakeTimeProvider();
        using var detector = new IdleDetector(Idle, Timeout, time);

        detector.Push(Utf8("primeiro\n"));
        time.Advance(Idle);
        var result = await detector.Completion;

        // result is an immutable snapshot, so asserting on it alone would hold even without
        // the guard. InAltScreen is the one piece of detector state still readable after the
        // turn ends, so it is what actually proves the late chunk was dropped.
        detector.Push(Utf8("\x1b[?1049htarde demais\n"));
        Assert.DoesNotContain("tarde", result.Text);
        Assert.False(detector.InAltScreen);
    }

    [Fact]
    public async Task Completion_ReportsTimeout_WhenTheTargetNeverSaysAnything()
    {
        var time = new FakeTimeProvider();
        using var detector = new IdleDetector(Idle, Timeout, time);

        // Nothing was ever pushed. Resolving as Idle here would be indistinguishable from
        // "the agent answered nothing"; the hard timeout is the honest report.
        time.Advance(Timeout);

        var result = await detector.Completion;
        Assert.Equal(TurnOutcome.Timeout, result.Outcome);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public void InAltScreen_ReflectsTheTargetsBuffer()
    {
        var time = new FakeTimeProvider();
        using var detector = new IdleDetector(Idle, Timeout, time);

        detector.Push(Utf8("\x1b[?1049h"));
        Assert.True(detector.InAltScreen);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj --filter "FullyQualifiedName~IdleDetectorTests"
```

Esperado: FALHA na compilação — `The type or namespace name 'IdleDetector' could not be found`.

- [ ] **Step 4: Write minimal implementation**

Criar `src/Orchestration.Core/Terminal/IdleDetector.cs`:

```csharp
namespace Orchestration.Core.Terminal;

public enum TurnOutcome
{
    /// <summary>The target stopped producing output for the whole idle window.</summary>
    Idle,
    /// <summary>The hard timeout fired first; <see cref="TurnResult.Text"/> is partial.</summary>
    Timeout,
    /// <summary>The target process went away mid-turn; <see cref="TurnResult.Text"/> is partial.</summary>
    TargetExited
}

public sealed record TurnResult(string Text, TurnOutcome Outcome);

/// <summary>
/// Watches one terminal's output and decides when a turn is over.
/// There is no reliable "the agent is done" signal, and a model thinking for two minutes is
/// indistinguishable from a hung one, so quiescence is the heuristic: no new bytes for the
/// idle window. The hard timeout bounds the wait either way and always hands back whatever
/// arrived, because a partial answer beats none for the agent that is blocked on it.
/// </summary>
public sealed class IdleDetector : IDisposable
{
    private readonly TimeSpan _idle;
    private readonly TimeProvider _time;
    private readonly AnsiFilter _filter = new();
    private readonly TurnCollapser _collapser = new();
    private readonly TaskCompletionSource<TurnResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();

    private ITimer? _idleTimer;
    private ITimer? _timeoutTimer;
    private long _lastPush;
    private bool _finished;

    public IdleDetector(TimeSpan idle, TimeSpan timeout, TimeProvider? time = null)
    {
        _idle = idle;
        _time = time ?? TimeProvider.System;

        // The idle timer stays disarmed until the first Push. Before the target has said
        // anything there is no quiescence to detect, and resolving as Idle with empty text
        // would tell the caller "it answered nothing" when the truth is "we never heard from
        // it". The hard timeout already bounds that case, with an outcome worth acting on.
        _idleTimer = _time.CreateTimer(_ => OnIdleElapsed(), null, System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
        _timeoutTimer = _time.CreateTimer(_ => Complete(TurnOutcome.Timeout), null, timeout, System.Threading.Timeout.InfiniteTimeSpan);
    }

    public Task<TurnResult> Completion => _completion.Task;

    public bool InAltScreen
    {
        get { lock (_gate) return _filter.InAltScreen; }
    }

    /// <summary>Feeds one raw chunk from the target's pseudoconsole. Safe to call from the reader thread.</summary>
    public void Push(ReadOnlySpan<byte> chunk)
    {
        lock (_gate)
        {
            if (_finished) return;
            _collapser.Append(_filter.Feed(chunk));
            _lastPush = _time.GetTimestamp();
            _idleTimer?.Change(_idle, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Change cannot recall a callback that already fired. Without this re-check, a Push that
    /// loses that race lets the stale callback end the turn microseconds after fresh output
    /// arrived, truncating the answer with no diagnostic — the exact failure this class exists
    /// to prevent. So the callback re-reads the clock and rearms instead of trusting itself.
    /// </summary>
    private void OnIdleElapsed()
    {
        lock (_gate)
        {
            if (_finished) return;

            TimeSpan since = _time.GetElapsedTime(_lastPush);
            if (since < _idle)
            {
                _idleTimer?.Change(_idle - since, System.Threading.Timeout.InfiniteTimeSpan);
                return;
            }
        }
        Complete(TurnOutcome.Idle);
    }

    /// <summary>Ends the turn early, e.g. when the target process exits.</summary>
    public void Complete(TurnOutcome outcome)
    {
        TurnResult result;
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
            result = new TurnResult(_collapser.Result, outcome);

            _idleTimer?.Dispose();
            _timeoutTimer?.Dispose();
            _idleTimer = null;
            _timeoutTimer = null;
        }
        _completion.TrySetResult(result);
    }

    public void Dispose() => Complete(TurnOutcome.TargetExited);
}
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj --filter "FullyQualifiedName~IdleDetectorTests"
```

Esperado: PASS, 8 testes.

- [ ] **Step 6: Run the whole suite**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj
```

Esperado: PASS, 30 testes (3 antigos + 11 + 8 + 8).

- [ ] **Step 7: Commit**

```bash
git add src/Orchestration.Core/Terminal/IdleDetector.cs tests/Orchestration.Core.Tests/Terminal/IdleDetectorTests.cs tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj
git commit -m "feat(core): detect end of turn by output quiescence"
```

- [ ] **Step 8: Validação manual contra o codex (risco #1 do spec)**

Isto não é automatizável barato e é o risco técnico principal da Fase 1. Rodar um pequeno harness manual: iniciar um `ConPtySession` com `powershell.exe -NoLogo -NoProfile -Command codex`, ligar `IdleDetector.Push` ao `OutputReceived`, escrever uma pergunta e imprimir `result.Text` e `result.Outcome`.

Registrar o achado em `docs/superpowers/specs/2026-07-28-fase1-mvp-design.md`, seção 16, na linha do risco de alt-screen: se `InAltScreen` ficar `true` e `Text` sair vazio, o risco se confirmou e a etapa E5 precisa decidir entre degradar com aviso ou antecipar o modelo de tela virtual. Repetir com `claude`, que é inline e deve funcionar.

---

## Parte B — Modelos e persistência (E1, Core)

### Task 4: Modelos do workspace

**Files:**
- Create: `src/Orchestration.Core/Models/Camera.cs`
- Create: `src/Orchestration.Core/Models/NodeBase.cs`
- Create: `src/Orchestration.Core/Models/Connection.cs`
- Create: `src/Orchestration.Core/Models/Workspace.cs`
- Create: `src/Orchestration.Core/Models/AppSettings.cs`
- Create: `src/Orchestration.Core/Persistence/TetherJson.cs`
- Test: `tests/Orchestration.Core.Tests/Models/WorkspaceJsonTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces: os tipos de `Orchestration.Core.Models` listados no spec §5, e `Orchestration.Core.Persistence.TetherJson.Options` (`JsonSerializerOptions`). Toda serialização do projeto — persistência **e** protocolo IPC da etapa E5 — usa esse mesmo `Options`.

- [ ] **Step 1: Write the failing test**

Criar `tests/Orchestration.Core.Tests/Models/WorkspaceJsonTests.cs`:

```csharp
using System.Text.Json;
using Orchestration.Core.Models;
using Orchestration.Core.Persistence;
using Xunit;

namespace Orchestration.Core.Tests;

public class WorkspaceJsonTests
{
    private static Workspace SampleWorkspace()
    {
        var terminal = new TerminalNode
        {
            Title = "claude",
            X = 10, Y = 20, Width = 720, Height = 420,
            CommandLine = "powershell.exe -NoLogo -NoExit -Command claude",
            WorkingDirectory = @"C:\dev\projeto",
            AutoStart = true
        };
        var note = new NoteNode
        {
            Title = "briefing",
            X = 800, Y = 20, Width = 340, Height = 240,
            FileName = "briefing.md",
            ViewMode = NoteViewMode.Raw
        };

        return new Workspace
        {
            Camera = new Camera { OffsetX = -40, OffsetY = 12, Zoom = 1.25 },
            Nodes = { terminal, note },
            Connections = { new Connection { SourceId = terminal.Id, TargetId = note.Id } }
        };
    }

    [Fact]
    public void Workspace_RoundTripsBothNodeKinds()
    {
        var original = SampleWorkspace();

        string json = JsonSerializer.Serialize(original, TetherJson.Options);
        var loaded = JsonSerializer.Deserialize<Workspace>(json, TetherJson.Options)!;

        var terminal = Assert.IsType<TerminalNode>(loaded.Nodes[0]);
        var note = Assert.IsType<NoteNode>(loaded.Nodes[1]);

        Assert.Equal("powershell.exe -NoLogo -NoExit -Command claude", terminal.CommandLine);
        Assert.Equal(@"C:\dev\projeto", terminal.WorkingDirectory);
        Assert.True(terminal.AutoStart);
        Assert.Equal("briefing.md", note.FileName);
        Assert.Equal(NoteViewMode.Raw, note.ViewMode);
        Assert.Equal(1.25, loaded.Camera.Zoom);
        Assert.Equal(terminal.Id, loaded.Connections[0].SourceId);
        Assert.False(loaded.Connections[0].Bidirectional);
    }

    [Fact]
    public void Workspace_IsWrittenAsDiffableJson()
    {
        string json = JsonSerializer.Serialize(SampleWorkspace(), TetherJson.Options);

        Assert.Contains("\"$type\": \"terminal\"", json);
        Assert.Contains("\"$type\": \"note\"", json);
        // Enums as names, not integers: the file is meant to be readable and hand-editable.
        Assert.Contains("\"Raw\"", json);
        Assert.Contains("\n", json);
    }

    [Fact]
    public void Workspace_DefaultsToTheCurrentVersion()
    {
        Assert.Equal(Workspace.CurrentVersion, new Workspace().Version);
        Assert.Equal(1.0, new Camera().Zoom);
    }

    [Fact]
    public void AppSettings_RoundTripsWithDefaults()
    {
        var defaults = new AppSettings();
        Assert.Equal(AppTheme.System, defaults.Theme);
        Assert.Equal(1500, defaults.IdleMs);
        Assert.Equal(120_000, defaults.AskTimeoutMs);
        Assert.Equal(5, defaults.MaxCallDepth);
        Assert.True(defaults.SeedAgentInstructions);

        string json = JsonSerializer.Serialize(defaults, TetherJson.Options);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json, TetherJson.Options)!;

        Assert.Equal(defaults.TerminalFontFamily, loaded.TerminalFontFamily);
        Assert.Equal(defaults.TerminalFontSize, loaded.TerminalFontSize);
        Assert.Contains("\"System\"", json);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj --filter "FullyQualifiedName~WorkspaceJsonTests"
```

Esperado: FALHA na compilação — `The type or namespace name 'Models' does not exist in the namespace 'Orchestration.Core'`.

- [ ] **Step 3: Write minimal implementation**

Criar `src/Orchestration.Core/Models/Camera.cs`:

```csharp
namespace Orchestration.Core.Models;

/// <summary>Where the viewport sits over the infinite canvas.</summary>
public sealed class Camera
{
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Zoom { get; set; } = 1.0;
}
```

Criar `src/Orchestration.Core/Models/NodeBase.cs` — o conjunto polimórfico é fechado e muda junto, então mora num arquivo só:

```csharp
using System.Text.Json.Serialization;

namespace Orchestration.Core.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TerminalNode), "terminal")]
[JsonDerivedType(typeof(NoteNode), "note")]
public abstract class NodeBase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string Title { get; set; } = "";
}

public sealed class TerminalNode : NodeBase
{
    public string CommandLine { get; set; } = "powershell.exe -NoLogo";

    /// <summary>Agents, notes and `tether ask` all key off this, so it is per node rather than global.</summary>
    public string WorkingDirectory { get; set; } = "";

    public bool AutoStart { get; set; }
}

public enum NoteViewMode { Raw, Preview }

public sealed class NoteNode : NodeBase
{
    /// <summary>File name inside the notes folder. The markdown itself never lives in workspace.json.</summary>
    public string FileName { get; set; } = "";

    public NoteViewMode ViewMode { get; set; } = NoteViewMode.Preview;
}
```

Criar `src/Orchestration.Core/Models/Connection.cs`:

```csharp
namespace Orchestration.Core.Models;

/// <summary>
/// A cable authorises a call, it does not carry bytes: `tether ask` only reaches a node the
/// caller is wired to. Direction is meaningful between terminals; for a note, any cable grants
/// both `note show` and `note edit`.
/// </summary>
public sealed class Connection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }
    public Guid TargetId { get; set; }
    public bool Bidirectional { get; set; }
}
```

Criar `src/Orchestration.Core/Models/Workspace.cs`:

```csharp
namespace Orchestration.Core.Models;

public sealed class Workspace
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public Camera Camera { get; set; } = new();
    public List<NodeBase> Nodes { get; set; } = new();
    public List<Connection> Connections { get; set; } = new();
}
```

Criar `src/Orchestration.Core/Models/AppSettings.cs`:

```csharp
namespace Orchestration.Core.Models;

public enum AppTheme { System, Light, Dark }

/// <summary>Lives in its own file: settings and workspace change at completely different rhythms.</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public string TerminalFontFamily { get; set; } = "Cascadia Mono, Consolas, monospace";
    public double TerminalFontSize { get; set; } = 14;
    public Dictionary<string, string> Shortcuts { get; set; } = new();

    /// <summary>Quiescence window that ends a turn, in milliseconds.</summary>
    public int IdleMs { get; set; } = 1500;

    /// <summary>Hard ceiling on a single `tether ask`, in milliseconds.</summary>
    public int AskTimeoutMs { get; set; } = 120_000;

    /// <summary>How deep a chain of agents calling agents may go before it is refused.</summary>
    public int MaxCallDepth { get; set; } = 5;

    /// <summary>Whether to seed the tether instruction block into AGENTS.md in a node's working directory.</summary>
    public bool SeedAgentInstructions { get; set; } = true;
}
```

Criar `src/Orchestration.Core/Persistence/TetherJson.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestration.Core.Persistence;

/// <summary>
/// One serializer configuration for the whole product — persisted files and the IPC protocol.
/// Two configurations that drift apart is how a workspace becomes unreadable by its own app.
/// </summary>
public static class TetherJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj --filter "FullyQualifiedName~WorkspaceJsonTests"
```

Esperado: PASS, 4 testes.

- [ ] **Step 5: Commit**

```bash
git add src/Orchestration.Core/Models src/Orchestration.Core/Persistence/TetherJson.cs tests/Orchestration.Core.Tests/Models
git commit -m "feat(core): add workspace, node and settings models"
```

---

### Task 5: AtomicFile

**Files:**
- Create: `src/Orchestration.Core/Persistence/AtomicFile.cs`
- Test: `tests/Orchestration.Core.Tests/Persistence/AtomicFileTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces:
  - `enum Orchestration.Core.Persistence.ReadOutcome { Primary, Backup, None }`
  - `static class Orchestration.Core.Persistence.AtomicFile` com `void Write(string path, string contents)` e `ReadOutcome TryRead<T>(string path, Func<string, T?> parse, out T? value) where T : class`.

A política de recuperação mora aqui, num lugar só: tenta o arquivo principal, depois o `.bak`, engolindo tanto erro de IO quanto erro de parse. `ReadOutcome` existe porque a UI precisa avisar quando caiu no backup.

- [ ] **Step 1: Write the failing test**

Criar `tests/Orchestration.Core.Tests/Persistence/AtomicFileTests.cs`:

```csharp
using System.Text.Json;
using Orchestration.Core.Persistence;
using Xunit;

namespace Orchestration.Core.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tether-tests-" + Guid.NewGuid().ToString("N"));

    private string Path0 => Path.Combine(_root, "sub", "data.json");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Box { public string Value { get; set; } = ""; }

    private static Box? Parse(string json) => JsonSerializer.Deserialize<Box>(json, TetherJson.Options);

    [Fact]
    public void Write_CreatesMissingDirectories()
    {
        AtomicFile.Write(Path0, "{}");
        Assert.True(File.Exists(Path0));
    }

    [Fact]
    public void Write_LeavesNoTemporaryFileBehind()
    {
        AtomicFile.Write(Path0, "{}");
        AtomicFile.Write(Path0, "{}");
        Assert.False(File.Exists(Path0 + ".tmp"));
    }

    [Fact]
    public void Write_KeepsThePreviousContentAsBackup()
    {
        AtomicFile.Write(Path0, "{\"Value\":\"primeiro\"}");
        AtomicFile.Write(Path0, "{\"Value\":\"segundo\"}");

        Assert.Contains("segundo", File.ReadAllText(Path0));
        Assert.Contains("primeiro", File.ReadAllText(Path0 + ".bak"));
    }

    [Fact]
    public void TryRead_ReadsThePrimaryFile()
    {
        AtomicFile.Write(Path0, "{\"Value\":\"ok\"}");

        var outcome = AtomicFile.TryRead<Box>(Path0, Parse, out var box);

        Assert.Equal(ReadOutcome.Primary, outcome);
        Assert.Equal("ok", box!.Value);
    }

    [Fact]
    public void TryRead_FallsBackToTheBackupWhenThePrimaryIsCorrupt()
    {
        AtomicFile.Write(Path0, "{\"Value\":\"bom\"}");
        AtomicFile.Write(Path0, "{\"Value\":\"tambem bom\"}");

        // Simulate a machine that died mid-write: valid .bak, garbage primary.
        File.WriteAllText(Path0, "{ isto nao e json");

        var outcome = AtomicFile.TryRead<Box>(Path0, Parse, out var box);

        Assert.Equal(ReadOutcome.Backup, outcome);
        Assert.Equal("bom", box!.Value);
    }

    [Fact]
    public void TryRead_ReturnsNoneWhenNothingIsUsable()
    {
        var outcome = AtomicFile.TryRead<Box>(Path0, Parse, out var box);

        Assert.Equal(ReadOutcome.None, outcome);
        Assert.Null(box);
    }

    [Fact]
    public void TryRead_ReturnsNoneWhenEverythingIsCorrupt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path0)!);
        File.WriteAllText(Path0, "lixo");
        File.WriteAllText(Path0 + ".bak", "lixo tambem");

        Assert.Equal(ReadOutcome.None, AtomicFile.TryRead<Box>(Path0, Parse, out _));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj --filter "FullyQualifiedName~AtomicFileTests"
```

Esperado: FALHA na compilação — `The name 'AtomicFile' does not exist`.

- [ ] **Step 3: Write minimal implementation**

Criar `src/Orchestration.Core/Persistence/AtomicFile.cs`:

```csharp
using System.Text;
using System.Text.Json;

namespace Orchestration.Core.Persistence;

public enum ReadOutcome
{
    /// <summary>The primary file parsed cleanly.</summary>
    Primary,
    /// <summary>The primary was missing or unusable and the .bak saved us.</summary>
    Backup,
    /// <summary>Nothing usable on disk; the caller should start fresh.</summary>
    None
}

/// <summary>
/// Crash-safe file replacement. Writes a sibling .tmp and swaps it in with File.Replace, which
/// is atomic and hands the previous good content to a .bak. Losing power mid-save would
/// otherwise leave a truncated workspace and nothing to fall back to.
/// </summary>
public static class AtomicFile
{
    public static void Write(string path, string contents)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temporary = path + ".tmp";
        File.WriteAllText(temporary, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(temporary, path);
    }

    /// <summary>
    /// Reads and parses <paramref name="path"/>, falling back to its .bak. A parse failure counts
    /// as unusable just like an IO failure: a syntactically broken file is the common corruption.
    /// </summary>
    public static ReadOutcome TryRead<T>(string path, Func<string, T?> parse, out T? value) where T : class
    {
        ReadOutcome[] outcomes = { ReadOutcome.Primary, ReadOutcome.Backup };
        string[] candidates = { path, path + ".bak" };

        for (int i = 0; i < candidates.Length; i++)
        {
            if (!File.Exists(candidates[i])) continue;
            try
            {
                T? parsed = parse(File.ReadAllText(candidates[i]));
                if (parsed is null) continue;
                value = parsed;
                return outcomes[i];
            }
            catch (Exception e) when (e is IOException or JsonException or ArgumentException or UnauthorizedAccessException)
            {
            }
        }

        value = null;
        return ReadOutcome.None;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj --filter "FullyQualifiedName~AtomicFileTests"
```

Esperado: PASS, 7 testes.

- [ ] **Step 5: Commit**

```bash
git add src/Orchestration.Core/Persistence/AtomicFile.cs tests/Orchestration.Core.Tests/Persistence/AtomicFileTests.cs
git commit -m "feat(core): add atomic file write with backup recovery"
```

---

### Task 6: TetherPaths e WorkspaceStore

**Files:**
- Create: `src/Orchestration.Core/Persistence/TetherPaths.cs`
- Create: `src/Orchestration.Core/Persistence/WorkspaceStore.cs`
- Test: `tests/Orchestration.Core.Tests/Persistence/WorkspaceStoreTests.cs`

**Interfaces:**
- Consumes: `Workspace`, `Camera`, `TerminalNode`, `NoteNode`, `Connection` (Task 4); `TetherJson.Options` (Task 4); `AtomicFile.Write`, `AtomicFile.TryRead`, `ReadOutcome` (Task 5).
- Produces:
  - `sealed class Orchestration.Core.Persistence.TetherPaths` com `TetherPaths(string? root = null)`, e as propriedades `Root`, `NotesFolder`, `WorkspaceFile`, `SettingsFile` (todas `string`).
  - `sealed class Orchestration.Core.Persistence.WorkspaceStore` com `WorkspaceStore(TetherPaths paths)`, `Workspace Load()`, `void Save(Workspace workspace)` e `ReadOutcome LastLoadOutcome { get; }`.

A raiz é injetável só por causa dos testes — em produção o construtor sem argumento resolve `%AppData%\Tether`, conforme o spec §5.

- [ ] **Step 1: Write the failing test**

Criar `tests/Orchestration.Core.Tests/Persistence/WorkspaceStoreTests.cs`:

```csharp
using Orchestration.Core.Models;
using Orchestration.Core.Persistence;
using Xunit;

namespace Orchestration.Core.Tests;

public class WorkspaceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tether-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TetherPaths _paths;

    public WorkspaceStoreTests() => _paths = new TetherPaths(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Paths_SitUnderTheGivenRoot()
    {
        Assert.Equal(Path.Combine(_root, "workspace.json"), _paths.WorkspaceFile);
        Assert.Equal(Path.Combine(_root, "settings.json"), _paths.SettingsFile);
        Assert.Equal(Path.Combine(_root, "notes"), _paths.NotesFolder);
    }

    [Fact]
    public void DefaultPaths_ResolveUnderRoamingAppData()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tether");
        Assert.Equal(expected, new TetherPaths().Root);
    }

    [Fact]
    public void Load_WithNoFile_ReturnsAnEmptyWorkspace()
    {
        var store = new WorkspaceStore(_paths);

        var workspace = store.Load();

        Assert.Equal(ReadOutcome.None, store.LastLoadOutcome);
        Assert.Empty(workspace.Nodes);
        Assert.Empty(workspace.Connections);
        Assert.Equal(1.0, workspace.Camera.Zoom);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheGraph()
    {
        var store = new WorkspaceStore(_paths);
        var terminal = new TerminalNode { Title = "claude", CommandLine = "cmd" };
        var note = new NoteNode { Title = "nota", FileName = "nota.md" };
        var original = new Workspace
        {
            Camera = new Camera { OffsetX = 5, OffsetY = 6, Zoom = 0.75 },
            Nodes = { terminal, note },
            Connections = { new Connection { SourceId = terminal.Id, TargetId = note.Id, Bidirectional = true } }
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(ReadOutcome.Primary, store.LastLoadOutcome);
        Assert.Equal(2, loaded.Nodes.Count);
        Assert.Equal("claude", Assert.IsType<TerminalNode>(loaded.Nodes[0]).Title);
        Assert.Equal("nota.md", Assert.IsType<NoteNode>(loaded.Nodes[1]).FileName);
        Assert.True(loaded.Connections[0].Bidirectional);
        Assert.Equal(0.75, loaded.Camera.Zoom);
    }

    [Fact]
    public void Load_RecoversFromTheBackupWhenThePrimaryIsCorrupt()
    {
        var store = new WorkspaceStore(_paths);
        store.Save(new Workspace { Nodes = { new NoteNode { Title = "sobrevivente", FileName = "a.md" } } });
        store.Save(new Workspace { Nodes = { new NoteNode { Title = "mais novo", FileName = "b.md" } } });

        File.WriteAllText(_paths.WorkspaceFile, "{ truncado");

        var loaded = store.Load();

        Assert.Equal(ReadOutcome.Backup, store.LastLoadOutcome);
        Assert.Equal("sobrevivente", loaded.Nodes[0].Title);
    }

    [Fact]
    public void Load_MigratesAVersionZeroFile()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_paths.WorkspaceFile,
            """
            {
              "Version": 0,
              "Camera": { "OffsetX": 0, "OffsetY": 0, "Zoom": 0 },
              "Nodes": [],
              "Connections": []
            }
            """);

        var workspace = new WorkspaceStore(_paths).Load();

        // A zero zoom would divide by zero the moment the canvas placed a node.
        Assert.Equal(1.0, workspace.Camera.Zoom);
        Assert.Equal(Workspace.CurrentVersion, workspace.Version);
    }

    [Fact]
    public void Save_StampsTheCurrentVersion()
    {
        var store = new WorkspaceStore(_paths);
        store.Save(new Workspace { Version = 0 });

        Assert.Contains($"\"Version\": {Workspace.CurrentVersion}", File.ReadAllText(_paths.WorkspaceFile));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj --filter "FullyQualifiedName~WorkspaceStoreTests"
```

Esperado: FALHA na compilação — `The name 'TetherPaths' does not exist`.

- [ ] **Step 3: Write minimal implementation**

Criar `src/Orchestration.Core/Persistence/TetherPaths.cs`:

```csharp
namespace Orchestration.Core.Persistence;

/// <summary>
/// Where the product keeps its data. The root is injectable purely so tests can point at a
/// throwaway directory instead of the real profile.
/// </summary>
public sealed class TetherPaths
{
    public TetherPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tether");
        NotesFolder = Path.Combine(Root, "notes");
    }

    public string Root { get; }
    public string NotesFolder { get; }
    public string WorkspaceFile => Path.Combine(Root, "workspace.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
}
```

Criar `src/Orchestration.Core/Persistence/WorkspaceStore.cs`:

```csharp
using System.Text.Json;
using Orchestration.Core.Models;

namespace Orchestration.Core.Persistence;

public sealed class WorkspaceStore
{
    private readonly TetherPaths _paths;

    public WorkspaceStore(TetherPaths paths) => _paths = paths;

    /// <summary>How the last <see cref="Load"/> went, so the UI can warn about a recovered file.</summary>
    public ReadOutcome LastLoadOutcome { get; private set; } = ReadOutcome.None;

    public Workspace Load()
    {
        LastLoadOutcome = AtomicFile.TryRead<Workspace>(
            _paths.WorkspaceFile,
            json => JsonSerializer.Deserialize<Workspace>(json, TetherJson.Options),
            out var workspace);

        return workspace is null ? new Workspace() : Migrate(workspace);
    }

    public void Save(Workspace workspace)
    {
        workspace.Version = Workspace.CurrentVersion;
        AtomicFile.Write(_paths.WorkspaceFile, JsonSerializer.Serialize(workspace, TetherJson.Options));
    }

    /// <summary>Linear migration: each case repairs its version, then falls through to the next.</summary>
    private static Workspace Migrate(Workspace workspace)
    {
        switch (workspace.Version)
        {
            case <= 0:
                if (workspace.Camera.Zoom <= 0) workspace.Camera.Zoom = 1.0;
                goto case 1;
            case 1:
            default:
                break;
        }

        workspace.Version = Workspace.CurrentVersion;
        return workspace;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj --filter "FullyQualifiedName~WorkspaceStoreTests"
```

Esperado: PASS, 7 testes.

- [ ] **Step 5: Commit**

```bash
git add src/Orchestration.Core/Persistence/TetherPaths.cs src/Orchestration.Core/Persistence/WorkspaceStore.cs tests/Orchestration.Core.Tests/Persistence/WorkspaceStoreTests.cs
git commit -m "feat(core): load and save the workspace with backup recovery"
```

---

### Task 7: SettingsStore

**Files:**
- Create: `src/Orchestration.Core/Persistence/SettingsStore.cs`
- Test: `tests/Orchestration.Core.Tests/Persistence/SettingsStoreTests.cs`

**Interfaces:**
- Consumes: `AppSettings`, `AppTheme` (Task 4); `TetherPaths` (Task 6); `AtomicFile`, `ReadOutcome` (Task 5).
- Produces: `sealed class Orchestration.Core.Persistence.SettingsStore` com `SettingsStore(TetherPaths paths)`, `AppSettings Load()`, `void Save(AppSettings settings)`.

Configurações corrompidas nunca devem impedir o app de abrir — o fallback é o default, não uma exceção.

- [ ] **Step 1: Write the failing test**

Criar `tests/Orchestration.Core.Tests/Persistence/SettingsStoreTests.cs`:

```csharp
using Orchestration.Core.Models;
using Orchestration.Core.Persistence;
using Xunit;

namespace Orchestration.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tether-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TetherPaths _paths;

    public SettingsStoreTests() => _paths = new TetherPaths(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Load_WithNoFile_ReturnsDefaults()
    {
        var settings = new SettingsStore(_paths).Load();

        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(14, settings.TerminalFontSize);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new SettingsStore(_paths);
        store.Save(new AppSettings
        {
            Theme = AppTheme.Dark,
            TerminalFontSize = 18,
            IdleMs = 900,
            Shortcuts = { ["novo-terminal"] = "Ctrl+T" }
        });

        var loaded = store.Load();

        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(18, loaded.TerminalFontSize);
        Assert.Equal(900, loaded.IdleMs);
        Assert.Equal("Ctrl+T", loaded.Shortcuts["novo-terminal"]);
    }

    [Fact]
    public void Load_WithACorruptFile_FallsBackToDefaultsInsteadOfThrowing()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_paths.SettingsFile, "nao e json");

        var settings = new SettingsStore(_paths).Load();

        Assert.Equal(AppTheme.System, settings.Theme);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj --filter "FullyQualifiedName~SettingsStoreTests"
```

Esperado: FALHA na compilação — `The name 'SettingsStore' does not exist`.

- [ ] **Step 3: Write minimal implementation**

Criar `src/Orchestration.Core/Persistence/SettingsStore.cs`:

```csharp
using System.Text.Json;
using Orchestration.Core.Models;

namespace Orchestration.Core.Persistence;

/// <summary>
/// Settings live in their own file because they change on a completely different rhythm from
/// the workspace. A broken settings file falls back to defaults: it must never stop the app
/// from opening.
/// </summary>
public sealed class SettingsStore
{
    private readonly TetherPaths _paths;

    public SettingsStore(TetherPaths paths) => _paths = paths;

    public AppSettings Load()
    {
        AtomicFile.TryRead<AppSettings>(
            _paths.SettingsFile,
            json => JsonSerializer.Deserialize<AppSettings>(json, TetherJson.Options),
            out var settings);

        return settings ?? new AppSettings();
    }

    public void Save(AppSettings settings) =>
        AtomicFile.Write(_paths.SettingsFile, JsonSerializer.Serialize(settings, TetherJson.Options));
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj --filter "FullyQualifiedName~SettingsStoreTests"
```

Esperado: PASS, 3 testes.

- [ ] **Step 5: Commit**

```bash
git add src/Orchestration.Core/Persistence/SettingsStore.cs tests/Orchestration.Core.Tests/Persistence/SettingsStoreTests.cs
git commit -m "feat(core): persist app settings in their own file"
```

---

### Task 8: Autosave

**Files:**
- Create: `src/Orchestration.Core/Persistence/Autosave.cs`
- Test: `tests/Orchestration.Core.Tests/Persistence/AutosaveTests.cs`

**Interfaces:**
- Consumes: nada (recebe um `Action`).
- Produces: `sealed class Orchestration.Core.Persistence.Autosave : IDisposable` com `Autosave(Action save, TimeSpan delay, TimeProvider? time = null)`, `void Touch()`, `void FlushNow()`.

Sem `Suspend`/`Resume`: o modelo só é tocado quando o drag **solta**, então não existe rajada durante o arrasto para suspender. Se um dia existir, aí sim.

- [ ] **Step 1: Write the failing test**

Criar `tests/Orchestration.Core.Tests/Persistence/AutosaveTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using Orchestration.Core.Persistence;
using Xunit;

namespace Orchestration.Core.Tests;

public class AutosaveTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    [Fact]
    public void Touch_SavesOnceTheDelayElapses()
    {
        var time = new FakeTimeProvider();
        int saves = 0;
        using var autosave = new Autosave(() => saves++, Delay, time);

        autosave.Touch();
        Assert.Equal(0, saves);

        time.Advance(Delay);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void Touch_ManyTimesInABurst_SavesOnce()
    {
        var time = new FakeTimeProvider();
        int saves = 0;
        using var autosave = new Autosave(() => saves++, Delay, time);

        for (int i = 0; i < 100; i++)
        {
            autosave.Touch();
            time.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.Equal(0, saves);
        time.Advance(Delay);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void FlushNow_SavesImmediatelyAndCancelsThePendingWrite()
    {
        var time = new FakeTimeProvider();
        int saves = 0;
        using var autosave = new Autosave(() => saves++, Delay, time);

        autosave.Touch();
        autosave.FlushNow();
        Assert.Equal(1, saves);

        time.Advance(Delay);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void FlushNow_WithNothingPending_DoesNotSave()
    {
        var time = new FakeTimeProvider();
        int saves = 0;
        using var autosave = new Autosave(() => saves++, Delay, time);

        autosave.FlushNow();
        Assert.Equal(0, saves);
    }

    [Fact]
    public void Dispose_DropsThePendingWrite()
    {
        var time = new FakeTimeProvider();
        int saves = 0;
        var autosave = new Autosave(() => saves++, Delay, time);

        autosave.Touch();
        autosave.Dispose();
        time.Advance(Delay);

        // Closing the app calls FlushNow explicitly; Dispose alone must not fire a write.
        Assert.Equal(0, saves);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj --filter "FullyQualifiedName~AutosaveTests"
```

Esperado: FALHA na compilação — `The name 'Autosave' does not exist`.

- [ ] **Step 3: Write minimal implementation**

Criar `src/Orchestration.Core/Persistence/Autosave.cs`:

```csharp
namespace Orchestration.Core.Persistence;

/// <summary>
/// Coalesces a burst of model changes into a single write. Typing in a note fires a change per
/// keystroke; without the debounce every one of them would rewrite workspace.json.
/// </summary>
public sealed class Autosave : IDisposable
{
    private readonly Action _save;
    private readonly TimeSpan _delay;
    private readonly object _gate = new();

    private ITimer? _timer;
    private bool _pending;
    private bool _disposed;

    public Autosave(Action save, TimeSpan delay, TimeProvider? time = null)
    {
        _save = save;
        _delay = delay;
        _timer = (time ?? TimeProvider.System).CreateTimer(
            _ => Fire(), null, System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>Marks the model dirty and (re)starts the debounce window.</summary>
    public void Touch()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = true;
            _timer?.Change(_delay, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes right now if anything is pending. Called when the window closes.</summary>
    public void FlushNow()
    {
        lock (_gate)
        {
            if (!_pending || _disposed) return;
            _pending = false;
            _timer?.Change(System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
        }
        _save();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (!_pending || _disposed) return;
            _pending = false;
        }
        _save();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj --filter "FullyQualifiedName~AutosaveTests"
```

Esperado: PASS, 5 testes.

- [ ] **Step 5: Run the whole suite**

```bash
dotnet test tests/Orchestration.Core.Tests/Orchestration.Core.Tests.csproj
```

Esperado: PASS, 56 testes (3 antigos + 11 + 8 + 8 + 4 + 7 + 7 + 3 + 5).

- [ ] **Step 6: Commit**

```bash
git add src/Orchestration.Core/Persistence/Autosave.cs tests/Orchestration.Core.Tests/Persistence/AutosaveTests.cs
git commit -m "feat(core): debounce workspace writes"
```

---

## Parte C — Ligar a persistência à janela (E1, App)

Estas duas tasks mexem em WinUI e não têm teste automatizado barato. A verificação é manual e está escrita passo a passo.

### Task 9: Quebrar MainWindow em partials

**Files:**
- Create: `src/Orchestration.App/MainWindow.Canvas.cs`
- Create: `src/Orchestration.App/MainWindow.Nodes.cs`
- Modify: `src/Orchestration.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: nada de novo.
- Produces: nada de novo. **Refatoração pura, zero mudança de comportamento.** O objetivo é abrir espaço: o arquivo tem 220 linhas hoje e passa de 600 com cabos, resize, edição de nó e persistência.

- [ ] **Step 1: Confirmar o ponto de partida**

```bash
git status
```

Esperado: `working tree clean`. Refatoração mecânica não se mistura com mudança pendente.

- [ ] **Step 2: Mover a região de canvas**

Criar `src/Orchestration.App/MainWindow.Canvas.cs` e mover para lá, **sem editar o corpo dos métodos**, tudo que hoje está sob os comentários `// ---- canvas transform ----` e `// ---- pan and zoom ----` de `MainWindow.xaml.cs`: os campos `MinZoom`, `MaxZoom`, `_zoom`, `_offsetX`, `_offsetY`, `_panStart`, `_panning`, e os métodos `PlaceNode`, `ApplyLayout`, `UpdateZoomLabel`, `OnViewportSizeChanged`, `OnCanvasPointerPressed`, `OnCanvasPointerMoved`, `OnCanvasPointerReleased`, `OnCanvasWheel`, `OnResetView`.

O arquivo começa assim:

```csharp
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Orchestration.App;

/// <summary>Camera: pan, zoom and where each node lands on screen.</summary>
public sealed partial class MainWindow
{
    // ... campos e métodos movidos, sem alteração
}
```

- [ ] **Step 3: Mover a região de nós**

Criar `src/Orchestration.App/MainWindow.Nodes.cs` e mover para lá a classe aninhada `CanvasNode`, os campos `_nodes` e `_spawnCursor`, e os métodos `AddNode`, `RemoveNode`, `RegisterDrag`, `OnNewTerminal`, `OnNewNote`.

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Orchestration.App.Views;
using Windows.Foundation;

namespace Orchestration.App;

/// <summary>Creating, removing and dragging the things on the canvas.</summary>
public sealed partial class MainWindow
{
    private sealed class CanvasNode
    {
        public required FrameworkElement View;
        public required INodeView Node;
        public double X, Y, Width, Height;
    }

    // ... campos e métodos movidos, sem alteração
}
```

- [ ] **Step 4: Enxugar o MainWindow.xaml.cs**

Sobra o construtor e o handler de `Closed`. O arquivo inteiro passa a ser:

```csharp
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
```

- [ ] **Step 5: Compilar**

```bash
dotnet build src/Orchestration.App/Orchestration.App.csproj
```

Esperado: `Build succeeded`, 0 erros.

- [ ] **Step 6: Verificar que nada mudou**

```bash
dotnet run --project src/Orchestration.App/Orchestration.App.csproj
```

Conferir na janela: abre com um terminal e uma nota; arrastar o fundo faz pan; Ctrl+roda dá zoom no cursor e o rótulo de porcentagem acompanha; arrastar o header move o nó; "Ajustar zoom" volta a 100%; digitar no terminal funciona. Fechar a janela.

- [ ] **Step 7: Commit**

```bash
git add src/Orchestration.App/MainWindow.xaml.cs src/Orchestration.App/MainWindow.Canvas.cs src/Orchestration.App/MainWindow.Nodes.cs
git commit -m "refactor(app): split MainWindow into canvas and node partials"
```

---

### Task 10: Carregar e salvar o workspace

**Files:**
- Modify: `src/Orchestration.App/MainWindow.xaml.cs`
- Modify: `src/Orchestration.App/MainWindow.Nodes.cs`
- Modify: `src/Orchestration.App/MainWindow.Canvas.cs`
- Modify: `src/Orchestration.App/MainWindow.xaml`

**Interfaces:**
- Consumes: `TetherPaths`, `WorkspaceStore`, `ReadOutcome`, `Autosave` (Tasks 5–8); `Workspace`, `Camera`, `NodeBase`, `TerminalNode`, `NoteNode` (Task 4).
- Produces: `CanvasNode` passa a carregar `public required NodeBase Model;`. As etapas E2 e E3 leem `entry.Model` para achar o nó e gravar mudanças.

A nota ainda guarda o markdown em memória — arquivo `.md` real é a etapa E2. Aqui a nota persiste só posição, tamanho e título.

- [ ] **Step 1: Dar um modelo ao CanvasNode**

Em `src/Orchestration.App/MainWindow.Nodes.cs`, trocar a classe aninhada e o `AddNode`. `X/Y/Width/Height` deixam de ser campos soltos e passam a viver no modelo — uma fonte de verdade só, senão o salvamento grava posição velha.

```csharp
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
```

Substituir `AddNode`, `OnNewTerminal` e `OnNewNote` por:

```csharp
/// <summary>Adds a node that already has a model — used both by the toolbar and by workspace load.</summary>
private void AddNode(FrameworkElement view, INodeView node, NodeBase model)
{
    var entry = new CanvasNode { View = view, Node = node, Model = model };

    _nodes.Add(entry);
    _workspace.Nodes.Add(model);
    World.Children.Add(view);
    PlaceNode(entry);
    node.ApplyZoom(_zoom);
    RegisterDrag(entry);
    _autosave.Touch();
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

    _nodes.Remove(entry);
    _workspace.Nodes.Remove(entry.Model);
    _workspace.Connections.RemoveAll(c => c.SourceId == entry.Model.Id || c.TargetId == entry.Model.Id);
    World.Children.Remove(view);
    _autosave.Touch();
}

private void OnNewTerminal(object sender, RoutedEventArgs e)
{
    var (x, y) = NextSpawnPoint();
    Materialize(new TerminalNode
    {
        Title = "terminal",
        X = x, Y = y, Width = 720, Height = 420,
        CommandLine = "powershell.exe -NoLogo",
        WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    });
}

private void OnNewNote(object sender, RoutedEventArgs e)
{
    var (x, y) = NextSpawnPoint();
    Materialize(new NoteNode { Title = "nota", X = x, Y = y, Width = 340, Height = 240 });
}

/// <summary>Builds the view for a model. The one place that knows model kind maps to view kind.</summary>
private void Materialize(NodeBase model)
{
    switch (model)
    {
        case TerminalNode terminalModel:
        {
            var view = new TerminalNodeView
            {
                CommandLine = terminalModel.CommandLine,
                StartDirectory = string.IsNullOrEmpty(terminalModel.WorkingDirectory) ? null : terminalModel.WorkingDirectory
            };
            view.CloseRequested += RemoveNode;
            AddNode(view, view, model);
            break;
        }
        case NoteNode:
        {
            var view = new NoteNodeView { Markdown = "# Nota\n\nTexto em markdown." };
            view.CloseRequested += RemoveNode;
            AddNode(view, view, model);
            break;
        }
    }
}
```

Adicionar no topo do arquivo: `using Orchestration.Core.Models;`.

- [ ] **Step 2: Gravar no modelo ao soltar o drag**

Ainda em `MainWindow.Nodes.cs`, dentro de `RegisterDrag`, trocar o `EndDrag` para marcar o autosave. Durante o arrasto `entry.X` já escreve no modelo, mas só ao soltar vale a pena tocar o disco.

```csharp
void EndDrag(object s, PointerRoutedEventArgs e)
{
    if (!dragging) return;
    dragging = false;
    ((UIElement)s).ReleasePointerCapture(e.Pointer);
    _autosave.Touch();
}
```

- [ ] **Step 3: Persistir a câmera**

Em `src/Orchestration.App/MainWindow.Canvas.cs`, no fim de `OnCanvasPointerReleased` e no fim de `OnResetView`, e no ramo do zoom em `OnCanvasWheel` (logo depois de `ApplyLayout();`), acrescentar:

```csharp
        SaveCamera();
```

E adicionar o método ao mesmo arquivo:

```csharp
private void SaveCamera()
{
    _workspace.Camera.OffsetX = _offsetX;
    _workspace.Camera.OffsetY = _offsetY;
    _workspace.Camera.Zoom = _zoom;
    _autosave.Touch();
}
```

- [ ] **Step 4: Ligar tudo no construtor**

Substituir `src/Orchestration.App/MainWindow.xaml.cs` inteiro por:

```csharp
using Microsoft.UI.Xaml;
using Orchestration.App.Views;
using Orchestration.Core.Models;
using Orchestration.Core.Persistence;

namespace Orchestration.App;

public sealed partial class MainWindow : Window
{
    private readonly WorkspaceStore _store = new(new TetherPaths());
    private readonly Autosave _autosave;
    private Workspace _workspace = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "Orchestration";

        _autosave = new Autosave(SaveWorkspace, TimeSpan.FromSeconds(1));

        LoadWorkspace();

        Closed += (_, _) =>
        {
            _autosave.FlushNow();
            _autosave.Dispose();
            foreach (var node in _nodes)
                if (node.Node is TerminalNodeView terminal) terminal.DisposeSession();
        };
    }

    private void LoadWorkspace()
    {
        _workspace = _store.Load();

        _offsetX = _workspace.Camera.OffsetX;
        _offsetY = _workspace.Camera.OffsetY;
        _zoom = _workspace.Camera.Zoom;

        // Materialize appends to _workspace.Nodes, so iterate a snapshot and start from empty.
        var saved = _workspace.Nodes.ToList();
        _workspace.Nodes.Clear();
        foreach (var model in saved) Materialize(model);

        UpdateZoomLabel();

        if (_store.LastLoadOutcome == ReadOutcome.Backup)
            ShowRecoveryNotice("O workspace principal estava corrompido. Recuperado a partir do backup.");

        // A first run has nothing to show, and an empty canvas teaches nothing.
        if (saved.Count == 0)
        {
            OnNewTerminal(this, new RoutedEventArgs());
            OnNewNote(this, new RoutedEventArgs());
        }
    }

    private void SaveWorkspace()
    {
        // Autosave fires on a timer thread; the model is only safe to read on the UI thread.
        DispatcherQueue.TryEnqueue(() => _store.Save(_workspace));
    }

    private void ShowRecoveryNotice(string message)
    {
        RecoveryBar.Message = message;
        RecoveryBar.IsOpen = true;
    }
}
```

- [ ] **Step 5: Adicionar a InfoBar ao XAML**

Em `src/Orchestration.App/MainWindow.xaml`, dentro do `<Grid x:Name="Viewport" ...>`, **depois** do `<Canvas x:Name="World" ... />`, acrescentar:

```xml
            <InfoBar x:Name="RecoveryBar"
                     Severity="Warning"
                     IsOpen="False"
                     VerticalAlignment="Top"
                     Margin="12" />
```

- [ ] **Step 6: Compilar**

```bash
dotnet build src/Orchestration.App/Orchestration.App.csproj
```

Esperado: `Build succeeded`, 0 erros.

- [ ] **Step 7: Verificar a persistência de ponta a ponta**

```bash
dotnet run --project src/Orchestration.App/Orchestration.App.csproj
```

1. Primeira abertura: aparece um terminal e uma nota (workspace vazio). Mover os dois para posições reconhecíveis, dar zoom para ~150% e criar um terminal a mais. Fechar a janela.
2. Conferir que o arquivo existe e está legível:

```bash
cat "$APPDATA/Tether/workspace.json"
```

Esperado: JSON indentado com `"Version": 1`, três entradas em `Nodes`, duas com `"$type": "terminal"` e uma com `"$type": "note"`, e `Camera.Zoom` perto de 1.5.

3. Abrir de novo: os três nós voltam nas mesmas posições e o zoom é o mesmo. Remover um nó pelo X do header e fechar.
4. Abrir de novo: o nó removido não voltou.
5. Testar a recuperação — corromper o arquivo principal de propósito e reabrir:

```bash
printf '{ truncado' > "$APPDATA/Tether/workspace.json"
dotnet run --project src/Orchestration.App/Orchestration.App.csproj
```

Esperado: a janela abre com o estado do `.bak` e a `InfoBar` amarela avisa da recuperação.

- [ ] **Step 8: Commit**

```bash
git add src/Orchestration.App/MainWindow.xaml src/Orchestration.App/MainWindow.xaml.cs src/Orchestration.App/MainWindow.Nodes.cs src/Orchestration.App/MainWindow.Canvas.cs
git commit -m "feat(app): load and autosave the workspace"
```

---

## Estado ao fim deste plano

Entregue: o pipeline de texto completo e testado (E4) e modelos + persistência com autosave ligados à janela (E1). O canvas passa a lembrar nós, posições e câmera entre sessões, e sobrevive a um arquivo corrompido.

Não entregue, cada um com seu próprio plano:

| Etapa | Conteúdo | Depende de |
|---|---|---|
| **E2** | Nota `.md` real em `notes\`, `FileSystemWatcher`, preview markdown, toggle raw/preview | este plano |
| **E3** | Cabos (portas, camada bézier, criar/selecionar/deletar), editar nó, resize por grip, criação por arrasto, `SplitButton` claude/codex | este plano |
| **E5** | `Orchestration.Cli`, `TetherServer` em named pipe, bloco de ambiente no `ConPtySession`, `AgentPrimer`, `ask` fim a fim | E3 + este plano |
| **E6** | Settings UI: tema, fonte do terminal (a página xterm precisa aceitar `family`), atalhos | este plano |

E2, E3 e E6 podem correr em paralelo depois deste plano. E5 é o último porque precisa da topologia de cabos (E3) para autorizar chamadas.
