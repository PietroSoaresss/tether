# Fase 1 (MVP) — Design

Data: 2026-07-28
Escopo: os 9 itens da Fase 1. Substitui a seção "F1–F4" do `PLANO.md` no que diverge — ver "Divergências do PLANO.md".

## 1. Objetivo

Canvas de orquestração de agentes com nós-terminal (ConPTY) e nós-nota (`.md` reais), ligados por cabos que **autorizam** comunicação. Agentes conversam chamando uma CLI própria (`tether`) injetada nos processos filhos, não por encanamento de stdout→stdin.

## 2. Estado atual

Funciona e está verificado:

- `ConPtySession` (`src/Orchestration.Core/Terminal/ConPtySession.cs`, ~330 linhas): PowerShell interativo, resize, job object com `KILL_ON_JOB_CLOSE`. O bug do `STARTF_USESTDHANDLES` (filho ignora o pseudoconsole e herda handles redirecionados do pai) está corrigido e coberto por teste de regressão — **não regredir**.
- Canvas com pan, zoom-no-cursor e nós arrastáveis (`src/Orchestration.App/MainWindow.xaml.cs`).
- `TerminalNodeView`: xterm.js em WebView2, `CoreWebView2Environment` compartilhado, ponte por `PostWebMessageAsString` com payload base64, coalescing de output. `Assets/term/index.html` já tem `ResizeObserver` + fit addon reportando `{t:"size",cols,rows}`.
- `NoteNodeView`: `TextBox` cru, só em memória.
- 3 testes no Core.

Não existe: `Models`, `Persistence`, `AnsiFilter`, cabos, CLI, IPC, settings, composition root.

## 3. Decisão que define o design

**O cabo é autorização, não tubo.**

O `PLANO.md` original encanava stdout de A no stdin de B, com `PipeEngine`, `ISegmenter` (Realtime/Turn) por conexão, backpressure com `Channel` bounded e disjuntor de storm. Com a CLI própria isso some: o agente A chama `tether ask B "..."`, o app entrega a pergunta no stdin de B, espera B ficar ocioso e devolve a resposta como stdout do `tether`. Uma chamada, limitada, sob controle do agente chamador.

Consequências:

- Cabo A→B significa "A pode chamar B". `tether list` só enumera vizinhos ligados.
- Cabo terminal↔nota significa "esse terminal pode `note show` / `note edit` essa nota".
- Recursão vira limite de profundidade da cadeia de chamadas (o app conhece o nó chamador), não detecção de ciclo em stream. `CycleGuard` sai.
- `AnsiFilter` e detecção de ociosidade **continuam obrigatórios** — são o que faz `ask` devolver texto legível.

Sobrevive do PLANO.md: zoom por layout (nunca `ScaleTransform` em WebView2), job object por sessão, escrita atômica com `.bak`, `AnsiFilter` como máquina de estados streaming.

## 4. Estrutura

```
src/Orchestration.Core/            net8.0, zero UI
  Models/       Workspace, NodeBase, TerminalNode, NoteNode, Connection, Camera, AppSettings
  Terminal/     ConPtySession (existe), AnsiFilter (novo), IdleDetector (novo), EnvironmentBlock (novo)
  Ipc/          TetherRequest, TetherResponse, PipeNaming
  Graph/        Authorization (quem pode chamar quem), CallChain (profundidade)
  Persistence/  WorkspaceStore, SettingsStore, NoteFiles
src/Orchestration.App/             WinUI 3
  Views/        MainWindow + partials (.Canvas .Nodes .Wires), TerminalNodeView, NoteNodeView,
                NodeEditDialog, SettingsDialog
  Services/     TetherServer (named pipe), NoteWatcher, ThemeService, AgentPrimer
src/Orchestration.Cli/             net8.0 console → tether.exe
tests/Orchestration.Core.Tests/    xunit
```

Dependências: `App → Core`, `Cli → Core` (só `Ipc/` e `Models/`). Core não conhece WinUI.

`MainWindow.xaml.cs` tem 220 linhas hoje e passa de 600 com esse escopo. Vira `partial` em quatro arquivos (`MainWindow.xaml.cs` com ctor/toolbar/teardown, `.Canvas.cs` com pan/zoom/`PlaceNode`, `.Nodes.cs` com criação/drag/resize/edição, `.Wires.cs` com portas e cabos). Sem mudança de API nem de XAML.

## 5. Modelo de dados e persistência

```
%AppData%\Tether\
  workspace.json
  workspace.json.bak
  settings.json
  notes\<slug>.md
```

`%AppData%` = `Environment.SpecialFolder.ApplicationData` (Roaming), conforme pedido.

```
Workspace      Version:int, Camera{OffsetX,OffsetY,Zoom}, Nodes:List<NodeBase>, Connections:List<Connection>
NodeBase       Id:Guid, X, Y, Width, Height, Title          ($type polimórfico via [JsonPolymorphic])
  TerminalNode   CommandLine, WorkingDirectory, AutoStart:bool
  NoteNode       FileName (relativo a notes\), ViewMode: Raw|Preview
Connection     Id:Guid, SourceId:Guid, TargetId:Guid, Bidirectional:bool = false
AppSettings    Theme: Light|Dark|System, TerminalFontFamily, TerminalFontSize,
               Shortcuts: Dictionary<string,string>, IdleMs:int, AskTimeoutMs:int,
               MaxCallDepth:int, SeedAgentInstructions:bool
```

`Connection` não tem `Mode` nem `AppendEnter` — cabo não transporta bytes.

Persistência:

- `System.Text.Json` indentado, polimorfismo nativo do .NET 8, sem Newtonsoft.
- Escrita atômica: grava `*.tmp` → `File.Replace` mantendo `.bak` da última versão válida.
- Load: falha → tenta `.bak` → falha → workspace vazio + `InfoBar` avisando.
- `Version` com switch de migração linear.
- Autosave com debounce de 1 s após mudança de modelo, e no fechamento. Nunca durante um drag; a posição do nó grava no modelo ao **soltar**.
- Runtime nunca serializado: `ConPtySession`, buffers, ponte WebView2. Ao reabrir, terminais voltam `NotStarted` (`AutoStart` opcional relança).

## 6. Nó nota (item 3)

- Conteúdo mora em `notes\<slug>.md`. Slug derivado do título, com sufixo numérico em colisão.
- Um `FileSystemWatcher` para a pasta inteira, debounce de 200 ms.
- **Loop de escrita**: a UI grava → o watcher dispara → recarregar jogaria o cursor. Guarda: antes de recarregar, compara o conteúdo do disco com o do editor; igual ⇒ ignora. Comparação de conteúdo em vez de flag de timing, que é frágil sob debounce.
- Toggle raw ↔ preview no header do nó, persistido em `ViewMode`.
- Preview: `CommunityToolkit.WinUI.Controls.MarkdownTextBlock` (Labs). **Timebox de meio dia**: se não renderizar bem, fallback é Markdig → `RichTextBlock` com subset (heading, bold, italic, code, lista, link) — ver Riscos.
- Deletar o nó pergunta se apaga o arquivo. Default: mantém o arquivo.
- Arquivo sumiu do disco → nó mostra "arquivo ausente" com botão recriar.

## 7. Cabos (itens 4 e 8)

Visual:

- `INodeView` cresce: `InputPort`, `OutputPort`, `ResizeGrip`. Nada de `Receive` — o cabo não entrega texto; `ask` escreve direto no stdin da sessão e `note edit` escreve no arquivo.
- Camada `Canvas x:Name="Wires"` no `Viewport`, **antes** do `World` na ordem z — cabos por baixo dos nós, sem interceptar clique.
- Um `Path` com bézier cúbica por conexão, pontos de controle horizontais. Posições derivadas de `X/Y/Width/Height` do nó em coordenadas de mundo, sem lookup na árvore visual. Redesenha em pan, zoom, drag e resize.
- Criar: pointer press na `OutputPort` → rubber band seguindo o cursor → release sobre um nó. Recusa self-loop e cabo duplicado.
- Selecionar: clique no `Path` (hit-test de stroke sai de graça). Selecionado fica na cor de acento; `Delete` remove.
- Right-click no `Path` → flyout: Bidirecional, Excluir.

Autorização (`Core/Graph/Authorization`): X pode chamar Y se existir cabo `X→Y`, ou cabo `Y→X` com `Bidirectional = true`. Para nota, direção é irrelevante: qualquer cabo entre terminal e nota autoriza `note show` e `note edit` daquela nota.

Editar nó: duplo-clique no header abre dialog — terminal: título, comando, pasta de trabalho (com picker); nota: título, arquivo. `Delete` no nó selecionado remove o nó e os cabos incidentes.

Resize: grip 14×14 no canto inferior direito, dentro do XAML de cada nó, exposto por `INodeView.ResizeGrip`. `RegisterResize` espelha o `RegisterDrag` existente: delta `/ _zoom`, mínimo 240×160 (terminal) e 160×100 (nota), depois `PlaceNode`. Cursor via `ProtectedCursor` no `UserControl` (acessível porque as views são subclasses nossas). No terminal não há mais nada a fazer: o `ResizeObserver` da página já reporta `{t:"size"}` e o handler chama `_session.Resize`. Única mudança na página: coalescer `reportSize` por `requestAnimationFrame`, para `fit.fit()` não rodar 60×/s durante o arrasto.

## 8. Criação de nós (itens 2 e 3)

- Botão "Novo terminal" vira `SplitButton` com `MenuFlyout`: PowerShell, Claude, Codex, e "Pasta de trabalho…". O corpo do botão repete a última escolha.
- Comando gerado: `powershell.exe -NoLogo -NoExit -Command claude` (idem `codex`), **não** `claude` cru. `CreateProcess` com `lpApplicationName` nulo não resolve `.cmd` via `PATHEXT`, e `claude` é um shim npm — falharia silenciosamente. Passando pelo shell, "não instalado" aparece dentro do terminal.
- Pasta de trabalho por nó é obrigatória neste escopo (agentes, notas e `ask` dependem dela). Picker no flyout, valor grudento entre criações, persistido no nó.
- **Arrasto define o tamanho**: o botão arma `_pending = (kind, commandLine)`; o cursor vira cruz e uma barra mostra "arraste para definir o tamanho · Esc cancela". Com `_pending` armado, `OnCanvasPointerPressed` inicia um rubber band (`Rectangle` tracejado) em vez do pan. No release, retângulo menor que 40 px em qualquer eixo conta como clique: usa o tamanho default e o stagger `_spawnCursor` atual. Conversão tela→mundo: `(x - _offsetX) / _zoom`. `Esc` cancela.

## 9. CLI e IPC (item 5)

### Superfície

```
tether list
tether ask <nó> <prompt>
tether note show <nome>
tether note edit <nome> [--append]      # conteúdo novo vem por stdin
```

`<nó>` aceita título ou GUID; título ambíguo é erro com a lista de candidatos.

`tether list` enumera **apenas os vizinhos autorizados** do nó chamador, uma linha por vizinho: `<guid>  <kind>  <título>`, onde `kind` é `terminal` ou `note`. É assim que o agente descobre com quem pode falar e quais notas pode ler — sem cabo, o canvas é invisível para ele.

### Transporte

Named pipe local: `\\.\pipe\tether-<pid do app>`. Sem rede, sem porta, sem prompt de firewall; a ACL default já restringe à sessão do usuário. O PID no nome permite duas instâncias do app.

JSON-lines, uma linha de request e uma de response:

```
→ { "cmd": "ask", "from": "<guid do nó chamador>", "args": { "target": "B", "prompt": "..." } }
← { "ok": true, "data": "..." }
← { "ok": false, "error": "no route", "data": "<parcial, quando houver>" }
```

`TetherServer` (App) mantém um `NamedPipeServerStream` com várias instâncias, loop de accept e uma `Task` por conexão. Comandos que tocam o grafo são resolvidos na UI thread via `DispatcherQueue`.

### Injeção nos filhos

`ConPtySession.Start` ganha `IReadOnlyDictionary<string,string>? extraEnvironment` e monta um bloco de ambiente Unicode (chaves ordenadas ordinal-ignore-case, `K=V\0` concatenado, `\0` final) passado em `lpEnvironment`. `CREATE_UNICODE_ENVIRONMENT` já está ligado; hoje passa `IntPtr.Zero` e o filho herda o ambiente do app.

Variáveis injetadas:

```
TETHER_PIPE=tether-<pid>
TETHER_NODE=<guid do nó>
PATH=<pasta do tether.exe>;<PATH original>
```

Sem essas variáveis, o `tether` sai com erro explícito ("não está rodando dentro de um nó do Tether") e código 1.

`tether.exe` é um apphost normal (~70 ms de startup, aceitável). AOT fica registrado como otimização se o custo por chamada incomodar.

### Priming dos agentes

PATH injetado não faz claude nem codex descobrirem a ferramenta. No start de um nó terminal, `AgentPrimer` garante um bloco delimitado em `<cwd>\AGENTS.md` (e em `CLAUDE.md`, se já existir) com os quatro comandos e a lista de vizinhos ligados:

```
<!-- tether:start -->
...
<!-- tether:end -->
```

O bloco é reescrito a cada start; o resto do arquivo fica intocado. Como isso escreve no repositório do usuário, é controlado por `SeedAgentInstructions` nas configurações (default ligado).

## 10. `ask` e detecção de ociosidade (item 6)

1. A executa `tether ask B "pergunta"`. A CLI manda o request e **bloqueia lendo a resposta**.
2. O app resolve B e valida autorização pelo cabo. Sem cabo → `no route`.
3. Valida a cadeia de chamadas: cada `ask` empilha um hop (`Core/Graph/CallChain`). Profundidade ≥ `MaxCallDepth` (default 5), ou nó repetido na cadeia → `call depth exceeded`. Isso substitui o `CycleGuard` do plano antigo.
4. Estado de B: não rodando → `target not running`.
5. O app anexa um `IdleDetector` ao `OutputProduced` de B e escreve `pergunta + "\r"` no stdin de B. `ConPtySession.Write` já serializa por `SemaphoreSlim`, então o teclado do usuário e o `ask` não se atropelam.
6. `IdleDetector` empurra os bytes pelo `AnsiFilter` e acumula o texto limpo; sem bytes novos por `IdleMs` (default 1500) o turno fecha.
7. Resposta: texto colapsado em `data`, `ok:true`. A CLI escreve em stdout e sai 0.
8. Timeout duro `AskTimeoutMs` (default 120000): responde `ok:false, error:"timeout"` **mas com o parcial em `data`**. A CLI escreve o parcial em stdout, o aviso em stderr e sai 0 — o agente chamador aproveita o que veio em vez de perder tudo.

Concorrência: um `ask` ativo por nó alvo. Chamadas seguintes entram numa fila FIFO com teto de 4; acima disso, `busy`.

O alvo morrer no meio do turno devolve o parcial com `error:"target exited"`.

## 11. `AnsiFilter` e `IdleDetector` (Core)

`AnsiFilter` é máquina de estados streaming, **não** regex por chunk: as sequências são cortadas na fronteira dos reads de 4 KB o tempo todo, então o parser mantém estado entre chunks.

- Corta CSI, OSC, DCS, APC, SOS, PM e C0 exceto `\r`, `\n`, `\t`.
- `Decoder` UTF-8 stateful — codepoint partido entre chunks é o caso comum, não a exceção.
- Rastreia alt-screen (`ESC[?1049h` / `l`) e expõe `InAltScreen`.
- Colapso de redraw: linhas idênticas consecutivas viram uma; sobrescritas por `\r` dentro da mesma linha ficam só com o estado final.

Motivo do colapso: remover ANSI da saída de uma TUI que se redesenha **não** produz transcript limpo, produz texto duplicado. A evidência do spike F0 mostra o PSReadLine redesenhando caractere a caractere (`echo P` → `echo PR` → `echo PROVA_CONP`, com `ESC[1;38H` entre cada).

Limitação conhecida e aceita nesta fase: extração correta exige modelo de tela virtual com diff de snapshots. A heurística cobre bem CLI inline (Claude Code); TUI em alt-screen (codex/ratatui) fica degradada — ver Riscos.

`IdleDetector`: `Push(byte[])` alimenta o filtro e reseta um timer; `IdleMs` sem push dispara `TurnComplete(text)`. Buffer com teto de 256 KB, truncando do início. Timeout duro é contado à parte, desde o início do turno.

## 12. Configurações (item 9)

`settings.json` ao lado do `workspace.json` — ritmos de mudança diferentes justificam dois arquivos.

- Tema: aplicado em `RequestedTheme` do elemento raiz; `System` segue o Windows.
- Fonte do terminal: família e tamanho. A página xterm hoje só aceita `{t:"font", size}`; precisa aceitar `family` também.
- Atalhos: dicionário ação→gesto aplicado como `KeyboardAccelerator`, sobre um conjunto fixo de ações — novo terminal, nova nota, ajustar zoom, deletar seleção, reiniciar terminal focado. Gesto em conflito é rejeitado no dialog.

## 13. Erros

| Situação | Comportamento |
|---|---|
| `workspace.json` corrompido | tenta `.bak`; falhando, workspace vazio + `InfoBar` |
| nota ausente no disco | nó mostra "arquivo ausente" + botão recriar |
| `CreateProcess` falha | overlay no nó com a mensagem (já implementado) |
| pipe indisponível / env ausente | CLI escreve erro em stderr, sai 1 |
| `ask` sem cabo | `no route` |
| `ask` em alvo parado | `target not running` |
| `ask` estourou o tempo | parcial em stdout, aviso em stderr, sai 0 |
| alvo morre durante o `ask` | parcial + `target exited` |
| cadeia de chamadas profunda demais | `call depth exceeded` |

## 14. Testes

Core, xunit, roda sem UI:

- `AnsiFilter` — property test: o mesmo stream partido em fronteiras aleatórias produz saída idêntica. Mais CSI/OSC/DCS, codepoint UTF-8 partido, toggle de alt-screen, colapso de redraw e de sobrescrita por `\r`.
- `IdleDetector` — dispara após `IdleMs`; push reseta o timer; teto de buffer; timeout duro independente.
- `WorkspaceStore` — roundtrip, polimorfismo `$type`, migração de versão, recuperação pelo `.bak`, atomicidade sob interrupção simulada.
- `EnvironmentBlock` — ordenação, terminação dupla, override de `PATH`.
- `Authorization` / `CallChain` — com e sem cabo, bidirecional, self-loop, nota em qualquer direção, profundidade, nó repetido.
- Protocolo `Ipc` — serialização de request/response, variáveis de ambiente ausentes.

Verificação manual (não vale automatizar barato): WebView2, ConPTY real, `FileSystemWatcher`, ida e volta real de `tether ask` entre dois `claude`.

## 15. Ordem de trabalho

| Etapa | Conteúdo | Depende de |
|---|---|---|
| **E1** | `Core/Models`, `WorkspaceStore`, `SettingsStore`, autosave, composition root, split do `MainWindow` em partials | — |
| **E2** | Nota real: `.md` em disco, watcher, preview, toggle | E1 |
| **E3** | Cabos: portas, camada bézier, criar/selecionar/deletar; editar nó; resize; criação por arrasto; `SplitButton` claude/codex | E1 |
| **E4** | `AnsiFilter` + `IdleDetector` + testes | — |
| **E5** | `Orchestration.Cli`, `TetherServer`, `EnvironmentBlock` no ConPTY, `AgentPrimer`, `ask` fim a fim | E3, E4 |
| **E6** | Settings: tema, fonte (inclui `family` na página xterm), atalhos | E1 |

E2, E3 e E4 são paralelizáveis depois de E1. E4 é o único trecho com risco técnico novo; o resto é trabalho conhecido.

## 16. Riscos

| Risco | Impacto | Mitigação |
|---|---|---|
| Codex (ratatui) roda em alt-screen; `AnsiFilter` não extrai texto útil | `ask` para um nó codex volta vazio ou ruidoso | Documentado como limitação da Fase 1. Claude Code é inline e não sofre. Tela virtual fica para a fase seguinte; validar com codex real cedo, em E4 |
| `MarkdownTextBlock` do Labs é novo e pode não renderizar bem | item 3 emperra | Timebox de meio dia; fallback Markdig → `RichTextBlock` com subset |
| `AgentPrimer` escreve no repositório do usuário | atrito, diff inesperado | Bloco sempre delimitado por marcadores; opção `SeedAgentInstructions` |
| `ask` bloqueia o agente chamador | agente parado por causa de um `less` aberto do outro lado | Timeout duro de 120 s devolvendo o parcial |
| Heurística de ociosidade confunde "pensando" com "terminou" | resposta cortada no meio | `IdleMs` configurável; parcial sempre entregue; sinais fortes (OSC 133) ficam para depois, sobre o mesmo mecanismo |
| Memória com N instâncias de WebView2 | 10+ terminais pesam | `CoreWebView2Environment` único (já feito); criação sob demanda |
| Processos órfãos | agentes zumbis | Job object com `KILL_ON_JOB_CLOSE` por sessão (já feito) |

## 17. Divergências do `PLANO.md`

- `PipeEngine`, `ISegmenter` (Realtime/Turn) por conexão, backpressure com `Channel` bounded e `CycleGuard` com disjuntor: **removidos**. A CLI substitui o encanamento; recursão vira profundidade de cadeia.
- `Connection` perde `Mode` e `AppendEnter`, ganha `Bidirectional`.
- Notas deixam de ser inline no `workspace.json` e viram arquivos `.md` de verdade.
- Local de dados: `%AppData%\Tether` (Roaming), não `LocalState` de MSIX — o app segue unpackaged nesta fase.
- MSIX e `.appinstaller` ficam fora da Fase 1.

## 18. Fora de escopo

Undo/redo, multi-workspace, scrollback persistido, modelo de tela virtual para alt-screen, minimapa, grupos de nós, renderer de terminal nativo, restauração de sessões ao reabrir, MSIX/assinatura/updates.
