# Plano de Arquitetura — Orquestrador de Agentes em Canvas (Windows)

## Contexto

Construir um app desktop Windows inspirado no Maestri (macOS): um canvas infinito onde nós-terminal (ConPTY reais, interativos) e nós-nota (markdown) convivem, com conexões visuais dirigidas que encanam o stdout de um terminal para o stdin de outro — para orquestrar CLIs de agentes (`claude`, `codex`) visualmente. Projeto greenfield: `C:\Users\pietr\dev\orchestration` está vazio. Stack fixa: **.NET 8 + WinUI 3 (Windows App SDK) + C#**. Tudo local: sem nuvem, sem telemetria. Este documento é o entregável — plano, sem código.

## Estado atual (F0 concluído — 27/07/2026)

Os dois riscos que poderiam matar o projeto foram testados e eliminados.

**Funcionando e verificado:**
- `ConPtySession` (P/Invoke próprio, ~330 linhas) — PowerShell interativo real, resize, job object.
- WinUI 3 compila e roda **sem Visual Studio**, só com o .NET SDK pela CLI (`dotnet build`).
- Canvas com pan, zoom no cursor e nós arrastáveis; nó terminal (xterm.js/WebView2) e nó nota.
- 3 testes no Core passando (`dotnet test`).

**Descoberta técnica que custou caro — não regredir:** o filho do ConPTY **ignora o pseudoconsole e herda os handles padrão do processo pai** sempre que estes estão redirecionados. Todas as chamadas nativas retornam sucesso, então a falha é silenciosa: o shell escreve no lugar errado e morre por EOF. A correção é setar `STARTF_USESTDHANDLES` em `STARTUPINFO.dwFlags` deixando `hStdInput/hStdOutput/hStdError` nulos ([microsoft/terminal #11276](https://github.com/microsoft/terminal/issues/11276)). Há teste de regressão cobrindo isso.

**Evidência do modo "por turno":** a saída crua do PowerShell capturada no spike mostra o PSReadLine redesenhando a linha caractere a caractere (`echo P` → `echo PR` → `echo PROVA_CONP`, com `ESC[1;38H` entre cada). Confirma que remover ANSI byte a byte produz texto duplicado, não transcript — o colapso de redraws do `TurnSegmenter` é obrigatório, não enfeite.

**Decisão revista:** ConPTY via P/Invoke vendorizado em vez de Porta.Pty — a API é pequena, não precisamos do peso cross-platform e o controle total foi o que permitiu achar o bug acima.

**Ambiente:** o .NET SDK 8.0.423 está em `%USERPROFILE%\.dotnet` (o UAC bloqueia instalação machine-wide em sessão não-interativa) e já foi adicionado ao PATH do usuário.

**Pendente:** persistência, conexões e pipe, markdown real nas notas, configurações, MSIX.

---

## Decisões-chave (TL;DR)

| Tema | Decisão | Por quê |
|---|---|---|
| Render do terminal | xterm.js hospedado em WebView2 (ambiente compartilhado) | Único caminho maduro no Windows (padrão VS Code/Tabby); renderer nativo é plano B caro |
| Zoom × WebView2 | Zoom "por layout" (posições/tamanhos/fonte × zoom), **não** `ScaleTransform` | WebView2 não é renderizado pelo XAML; transform de escala tem bugs documentados de repaint |
| ConPTY | Porta.Pty (NuGet) como candidato; fallback: P/Invoke vendorizado (~1 arquivo) | API ConPTY é pequena; wrapper só se se provar sólido no spike |
| Canvas | Feito à mão: `Canvas` + `CompositeTransform` + eventos de ponteiro | Não existe lib de node-graph madura para WinUI 3 (Nodify/NodeNetwork são WPF); pan/zoom é ~200 linhas |
| Pipe | Pipeline de estágios com 2 modos por conexão; **default = por turno** (quiescência) | CLIs de agente são TUIs que se redesenham; byte-a-byte entre elas gera ruído. Realtime é o caso degenerado do mesmo pipeline |
| Loops A→B→A | **Permitidos**, com disjuntor (circuit breaker) no engine | Conversa entre dois agentes é O caso de uso; o perigo é o storm infinito, não o ciclo em si |
| Persistência | JSON (`System.Text.Json`) em LocalState, escrita atômica, autosave | Stdlib, legível, versionável |
| Distribuição | MSIX single-project + `.appinstaller` | Pedido; `runFullTrust` para ConPTY |

---

## 1. Estrutura de projeto

```
orchestration.sln
├─ src/Orchestration.Core/          # net8.0 puro, zero dependência de UI — testável headless
│  ├─ Models/        # Workspace, NodeBase, TerminalNode, NoteNode, Connection, Camera, AppSettings
│  ├─ Terminal/      # ConPtySession, JobObjectGuard, AnsiFilter (parser VT streaming)
│  ├─ Piping/        # PipeEngine, PipeConnection, ISegmenter (Realtime|Turn), CycleGuard
│  └─ Persistence/   # WorkspaceStore, SettingsStore (atômico, versionado)
├─ src/Orchestration.App/           # WinUI 3 (Windows App SDK), packaged
│  ├─ Views/         # MainWindow, CanvasHost, TerminalNodeView, NoteNodeView, ConnectionLayer, SettingsDialog
│  ├─ ViewModels/    # WorkspaceViewModel, TerminalNodeViewModel, NoteNodeViewModel, ConnectionViewModel, SettingsViewModel
│  ├─ Services/      # TerminalBridge (WebView2 ↔ sessão), ThemeService, ShortcutService
│  └─ Assets/term/   # dist do xterm.js + index.html locais (offline, sem CDN)
└─ tests/Orchestration.Core.Tests/  # xunit: AnsiFilter, segmentadores, CycleGuard, persistência
```

- Regra de dependência: `App → Core`; Core não conhece WinUI. Toda a lógica arriscada (filtro ANSI, segmentação de turnos, detecção de ciclo, persistência) vive no Core exatamente para ser testável sem UI.
- MVVM com `CommunityToolkit.Mvvm` (`ObservableObject`, `RelayCommand`). Sem framework de DI pesado: um composition root simples no `App.xaml.cs` resolve — são ~6 serviços.

## 2. Bibliotecas candidatas

| Função | Recomendado | Alternativa | Observações |
|---|---|---|---|
| Canvas infinito pan/zoom | Próprio: `Canvas` + `CompositeTransform`, `PointerWheelChanged` (zoom no cursor), drag em espaço vazio (pan) | `ScrollViewer` com `ZoomMode` — rejeitado (extent finito, briga com gestos custom) | Zoom-para-cursor é 3 linhas de matemática; nada no ecossistema WinUI 3 justifica dependência |
| Linhas de conexão | `Path` XAML (Bézier cúbica) | Win2D `CanvasControl` se passar de ~500 edges | `Path` tem hit-test de stroke de graça (clicar para selecionar/deletar) |
| ConPTY | [Porta.Pty](https://github.com/tomlm/Porta.Pty) (NuGet, usa ConPTY no Windows) | P/Invoke direto vendorizado: `CreatePseudoConsole`/`ResizePseudoConsole`/`ClosePseudoConsole` + `STARTUPINFOEX` (referências: [ConPty.Sample](https://github.com/akobr/ConPty.Sample), sample MiniTerm do microsoft/terminal) | Validar Porta.Pty no spike F0; se falhar em qualquer detalhe, o fallback é ~1 arquivo. Em ambos os casos: Job Object com `KILL_ON_JOB_CLOSE` por sessão |
| Render de terminal | xterm.js (+ addon fit) em WebView2; assets locais via `SetVirtualHostNameToFolderMapping`; ponte via `PostWebMessageAsString` (payload base64) | Nativo: parser VT (vtnetcore/XtermSharp) + renderer Win2D — plano B, custo alto | `CoreWebView2Environment` único compartilhado entre todos os nós (1 família de processos de browser) |
| Markdown | `CommunityToolkit.Labs` MarkdownTextBlock (novo, baseado em Markdig — [Labs #606](https://github.com/CommunityToolkit/Labs-Windows/issues/606)) | [CommunityToolkit.WinUI.UI.Controls.Markdown 7.1.2](https://www.nuget.org/packages/CommunityToolkit.WinUI.UI.Controls.Markdown) (linha 7.x, estável) | Decidir no spike pelo que renderizar melhor; edição = toggle TextBox ↔ preview (WYSIWYG real fica fora do MVP) |
| MVVM | `CommunityToolkit.Mvvm` | — | — |
| JSON | `System.Text.Json` | — | Polimorfismo nativo no .NET 8 (`[JsonPolymorphic]`/`[JsonDerivedType]`) — sem Newtonsoft |

## 3. Modelo de dados

Persistido (Core/Models, DTOs simples):

- **Workspace**: `Version:int`, `Camera { OffsetX, OffsetY, Zoom }`, `Nodes: List<NodeBase>`, `Connections: List<Connection>`
- **NodeBase** (abstrata, discriminador `$type` no JSON): `Id:Guid`, `X, Y, Width, Height`, `Title`
  - **TerminalNode**: `CommandLine` (ex.: `pwsh.exe`, `claude`), `StartDirectory?`, `AutoStart:bool`. Estado de execução e scrollback **não** são persistidos no MVP.
  - **NoteNode**: `Markdown:string` (inline no JSON do workspace; arquivos-satélite só se notas ficarem enormes — pós-MVP)
- **Connection**: `Id:Guid`, `SourceId:Guid` (stdout), `TargetId:Guid` (stdin), `Mode: Realtime | Turn`, `AppendEnter:bool` (default true), `Enabled:bool`
- **AppSettings** (arquivo próprio): `Theme: Light|Dark|System`, `TerminalFontFamily`, `TerminalFontSize`, `Shortcuts: Dictionary<ação, tecla>`

Runtime (nunca serializado): `ConPtySession` (handles, estado `NotStarted → Running → Exited(code) | Killed`), buffers de pipe, ponte WebView2. ViewModels embrulham os DTOs; posição do nó grava no modelo ao **soltar** o drag, não a cada pixel.

## 4. Persistência

- Local: `ApplicationData.Current.LocalFolder` (MSIX → `%LocalAppData%\Packages\<família>\LocalState`): `workspace.json` + `settings.json` (ritmos de mudança diferentes justificam dois arquivos; nada além disso).
- JSON indentado (diffável), campo `Version` para migração linear no load (switch por versão).
- Escrita atômica: grava `*.tmp` → `File.Replace` mantendo `.bak` da última versão válida. Se o load falhar, tenta o `.bak`.
- Autosave: debounce ~1 s após qualquer mudança de modelo + save no fechamento. Nunca durante um drag.
- MVP = 1 workspace único carregado ao abrir. Multi-workspace / salvar-como é pós-MVP.

## 5. Arquitetura do pipe stdout→stdin (com tratamento ANSI)

Fluxo por sessão — **um** read loop por PTY, fan-out por `Channel`:

```
ConPtySession A ──read loop (1 thread)──► broadcast:
   ├─► Channel de render ─► TerminalBridge ─► xterm.js do nó A     (bytes CRUS: cores/cursor preservados na tela)
   └─► Channel(s) de pipe ─► PipeEngine, por conexão:
          AnsiFilter ─► Segmenter (Realtime|Turn) ─► CycleGuard ─► escrita serializada no stdin de B
```

- **AnsiFilter** — máquina de estados streaming, **não** regex por chunk: sequências (CSI/OSC/DCS/APC e C0, exceto `\r\n\t`) são cortadas na fronteira dos reads de ~4 KB o tempo todo; o parser mantém estado entre chunks. Decodificação UTF-8 com `Decoder` stateful (codepoint partido entre chunks é o caso comum). O filtro também rastreia flags do stream: **alt-screen** (`ESC[?1049h` → pipe suspende; um editor fullscreen não produz nada útil de encanar).
- **Insight que define o design**: remover ANSI da saída de uma TUI que se redesenha (claude/codex redesenham spinner/status a cada frame) **não** produz um transcript limpo — produz texto duplicado. Por isso o modo Turn colapsa redraws (dedupe de linhas idênticas consecutivas + descarte de linhas efêmeras de spinner). Extração perfeita exigiria um modelo de tela virtual com diff de snapshots — pós-MVP (e é o degrau natural para o renderer nativo).
- **Escrita no target**: `SemaphoreSlim` por sessão — o teclado do usuário e N pipes concorrem pelo mesmo stdin; escrita é sempre serializada.
- **Backpressure**: `Channel` bounded (ex.: 256 chunks) por conexão. Cheio → o pipe pausa e a edge ganha badge "represado"; **nunca** bloqueia o read loop nem o render. Usuário decide: aguardar, limpar buffer ou desconectar.

## 6. Fases incrementais

| Fase | Conteúdo | Critério de saída |
|---|---|---|
| **F0 — Spike** (3–5 d) | ConPTY rodando `pwsh` e `claude` interativos numa janela crua; xterm.js em WebView2; teste de zoom-por-layout; validar Porta.Pty e MarkdownTextBlock | Decisão final de render/zoom com evidência |
| **F1 — Canvas** (~1,5 sem) | Pan/zoom/zoom-no-cursor; criar/arrastar/redimensionar/deletar nós; nota com toggle edição↔preview; persistência + autosave; tema claro/escuro (Mica, Fluent) | Workspace de notas utilizável de ponta a ponta |
| **F2 — Terminal** (~2 sem) | Nó terminal completo: spawn/kill/restart, input de teclado, resize → `ResizePseudoConsole` (debounced), fonte configurável, Job Objects, badges de estado | Vários `claude` interativos simultâneos no canvas |
| **F3 — Conexões e pipe** (~2 sem) | Portas nos nós + drag de edge + editar/deletar; PipeEngine modo Realtime; AnsiFilter; modo Turn (quiescência); config por conexão | A(`claude`) → B(`claude`) conversando limpo |
| **F4 — Segurança e acabamento** (~1,5 sem) | CycleGuard + disjuntor; ciclo de vida completo (crash/exit/religação); Settings (tema/fonte/atalhos); MSIX + `.appinstaller` | MVP instalável numa máquina limpa |

Total: ~7–8 semanas (dev solo, ordem de grandeza).

**Pós-MVP (backlog)**: undo/redo; multi-workspace; scrollback persistido; extração por modelo de tela virtual; transformação de texto na edge (templates/filtros regex); bracketed paste consciente do modo do target; OSC 133 (shell integration) como sinal forte de fim de turno; minimapa; grupos de nós; renderer de terminal nativo (Win2D); restauração de sessões ao reabrir.

## 7. Riscos técnicos e mitigação

| Risco | Impacto | Mitigação |
|---|---|---|
| WebView2 não participa do render XAML: `ScaleTransform` tem bugs de repaint/sincronização documentados ([WebView2Feedback #5400](https://github.com/MicrosoftEdge/WebView2Feedback/issues/5400), [guidance da MS](https://learn.microsoft.com/en-us/windows/apps/develop/performance/optimize-animations-and-media)) | Zoom do canvas quebraria nos terminais | **Não escalar o WebView2.** Zoom "por layout": posições e tamanhos dos nós × zoom + `fontSize` do xterm ∝ zoom, com refit ao fim do gesto; durante o gesto, placeholder barato (snapshot). Plano B: renderer nativo. Spike F0 valida |
| Memória com N instâncias de WebView2 | 10+ terminais ficam pesados | `CoreWebView2Environment` único; criação sob demanda; `TrySuspend`/placeholder para nós fora da viewport |
| ConPTY: chunks de ~4 KB, sem flow control, UTF-8/sequências partidas | Lixo ou perda no pipe | `Decoder` stateful + parser streaming; canais bounded |
| Flood de output na UI thread (MB/s de um build, por ex.) | App congela | Read em thread própria; coalescing para o render a cada ~16 ms; jamais evento por byte |
| Processos órfãos (árvore sobrevive a crash do app) | conhost/agentes zumbis | Job Object com `KILL_ON_JOB_CLOSE` por sessão — mata a árvore até em crash |
| Concorrência entre múltiplos PTYs | Races/deadlocks | Modelo fixo e documentado: 1 reader thread por sessão; fan-out por `Channel`; grafo mutado só na UI thread com snapshot imutável para o engine; 1 semáforo de escrita por sessão |
| Perf do canvas com muitos nós/edges | Jank | XAML aguenta centenas de elementos; colapsar nós fora da viewport; edges redesenham só em mudança de topologia/posição; Win2D como upgrade |
| Loop infinito entre agentes | Custo de API + storm | Disjuntor — item 11 |

## 8. Empacotamento e distribuição

- MSIX single-project packaging (padrão WinUI 3), capability `runFullTrust` (necessária para ConPTY/process spawn), `win-x64` (arm64 depois), Windows App SDK **self-contained** (usuário não instala runtime).
- Assinatura e updates: certificado próprio + distribuição via `.appinstaller` (URL ou pasta de rede) com update automático do Windows; Microsoft Store como alternativa que elimina a fricção do certificado — o app continua 100 % local.
- Sem telemetria: nenhum SDK de analytics; o único tráfego de rede do produto é zero (a checagem de update do `.appinstaller` é feita pelo Windows e é opcional).

## 9. Ciclo de vida dos processos

- Estados por nó: `NotStarted → Running → Exited(code) | Killed`, badge no header do nó; ao sair, overlay "Processo saiu (código X) — [Reiniciar]".
- **Source do pipe morre**: o pipe entrega o que restou no buffer, a edge fica esmaecida ("fonte parada"); o target segue intocado.
- **Target morre**: escrita no stdin falha (broken pipe) → capturada; edge esmaecida; entregas seguintes descartadas com badge.
- **Religação automática**: `Connection` referencia `NodeId`, não sessão. O `PipeEngine` resolve a sessão viva no momento da entrega ⇒ reiniciar qualquer ponta religa o pipe sozinho, sem estado extra.
- **Travado ≠ morto**: nenhuma heurística de hang — um agente "pensando" por minutos é indistinguível de travado. Sempre disponíveis: **Ctrl+C** (envia `\x03` no stdin) e **Kill** (mata a árvore inteira via Job Object).
- **Fechar o app**: dialog de confirmação se houver processos vivos; dispose fecha os ConPTYs e os Job Objects garantem a morte das árvores mesmo se o app crashar.
- Ao reabrir o workspace, terminais voltam `NotStarted` (`AutoStart` opcional por nó relança o comando).

## 10. Quando encanar: tempo real vs. por turno

- **Realtime (byte a byte sanitizado)**: latência zero; ótimo para stream contínuo (logs → shell que grava/filtra). Entre duas TUIs de agente, porém, entrega fragmentos de prompt, redraws e spinners — o agente-alvo recebe ruído intercalado com o próprio input.
- **Por turno (default)**: acumula a saída do source e descarrega quando ele "parece ter terminado" — **quiescência**: nenhum output novo por ~1 s (configurável por conexão). Sinais fortes (regex de prompt por nó, OSC 133 quando o shell emitir) entram pós-MVP por cima do mesmo mecanismo. A entrega é o texto colapsado (dedupe de redraw) + Enter final configurável (`AppendEnter`) — que é o que submete o turno num CLI de agente.
- **Impacto arquitetural**: a escolha troca **um único estágio** do pipeline — o `ISegmenter` (interface com exatamente duas implementações reais, por isso se justifica): `RealtimeSegmenter` = flush imediato (caso degenerado, ~10 linhas); `TurnSegmenter` = buffer + timer de quiescência + colapso de redraws. Filtro, guarda de ciclo e entrega serializada são idênticos nos dois modos. Alternável ao vivo por conexão, sem reconstruir nada.

## 11. Loops de conexão (A→B→A)

- **Não proibir ciclos**: dois agentes conversando (A↔B) é o caso de uso central do produto. Proibida apenas a **self-loop** (A→A) na criação — storm imediato, zero uso legítimo.
- **Na criação da edge**: DFS no grafo de conexões; se a nova edge fecha um ciclo → permitir com aviso ("isto cria um loop de conversa; trocas automáticas serão limitadas") + badge de ciclo nas edges envolvidas.
- **Em runtime — disjuntor**: como o `PipeEngine` é o mediador central, cada entrega carrega metadados **internos** (fora do byte stream, que não tem canal para isso): cadeia causal + hop count. Regras: (a) máx. **K trocas automáticas consecutivas** num ciclo sem input humano do usuário (default 10); (b) janela de taxa (máx. M entregas/min por edge, vale também para Realtime, onde se conta por rajada). Estourou → os pipes do ciclo **pausam**, edges ficam laranja com botão "Continuar (+K trocas)". Humano no loop por design — de quebra controla o custo de API dos agentes.
- Digitar manualmente em qualquer terminal do ciclo zera o contador (input humano = novo turno legítimo).

## Verificação (critérios de aceite do MVP)

1. **F0**: digitar interativamente em `pwsh` e `claude` no protótipo; decisão de zoom registrada com evidência do teste.
2. **F1**: criar 20 notas, pan/zoom fluido, fechar e reabrir → posições, texto e câmera intactos; matar o app no meio de um save não corrompe o workspace (`.bak` recupera).
3. **F2**: 5 terminais com `claude` simultâneos; resize sem lixo visual; Kill não deixa órfãos (conferir no Task Manager); crash forçado do app mata as árvores de processo.
4. **F3**: A(`claude`) → B(`claude`) em modo Turn: 3 trocas limpas, sem sequência ANSI vazada nem linha duplicada de redraw; em Realtime: `ping -t` encanado para um shell que registra em arquivo.
5. **F4**: ciclo A→B→A pausa após K trocas com badge e retomada manual; matar o source com pipe ativo não derruba o app nem o target; instalar via MSIX numa máquina limpa e repetir os testes 2–4.
6. **Testes unitários no Core** (rodam no CI sem UI): AnsiFilter com sequências partidas em fronteiras arbitrárias (teste propriedade: split aleatório do mesmo stream ⇒ mesmo resultado); TurnSegmenter (quiescência, dedupe); CycleGuard (grafos com/sem ciclo, self-loop); WorkspaceStore (roundtrip + migração de versão + recuperação de `.bak`).
