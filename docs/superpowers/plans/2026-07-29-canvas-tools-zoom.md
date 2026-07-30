# Plano — Escolha de agente, criação por arraste, ferramentas tipo Excalidraw e zoom

Data: 2026-07-29

## Contexto

Cinco mudanças pedidas no canvas do Tether. Duas delas (escolha de agente, tamanho de texto) já
existem parcialmente e precisam de ajuste, não de construção; três são trabalho novo. Este plano
separa o que já está feito do que falta, porque reconstruir o que existe seria a pior saída.

Restrições que valem para tudo aqui: `DESIGN.md` reserva **violeta para estrutura, lime para
atividade/seleção e vermelho para erro/destruição** — nenhuma cor nova entra para diferenciar tipo de
agente. E `PRODUCT.md` princípio 5 mais `DESIGN.md` fixam piso de 12 px para terminal e nota durante o
zoom (já implementado em `TerminalNodeView.ApplyZoom` e `NoteNodeView.ApplyZoom`).

## O que já existe (não reconstruir)

| Peça | Onde | Estado |
|---|---|---|
| Escolha PowerShell/Claude/Codex | [MainWindow.xaml:54-67](src/Orchestration.App/MainWindow.xaml) `SplitButton` + `MenuFlyout` | Existe, mas o **clique principal não pergunta** — cria com o último tipo escolhido (`_shellKind`, default `powershell`) |
| Tamanho de texto 10–48 | [MainWindow.Tools.cs:97-101](src/Orchestration.App/MainWindow.Tools.cs) `_textSize` | Existe, mas só aplica a **texto novo**; não há como alterar um texto já criado |
| Traço livre (lápis) + borracha | `MainWindow.Tools.cs`, `CanvasItemKind.Stroke` | Funciona, persistido |
| Máquina de estados de ferramenta | `TryStartCanvasTool` / `TryMoveCanvasTool` / `TryEndCanvasTool` | É o encaixe natural para tudo que falta |
| Zoom no cursor + grade + persistência de câmera | `MainWindow.Canvas.cs` | Funciona; os limites estão errados (item 5) |

## Fundação compartilhada

Três peças que os cinco itens reusam. Fazer estas primeiro evita duplicação.

### F1. Tabela única de tipos de agente

Hoje o conhecimento de "que tipos de terminal existem" está espalhado em **quatro** lugares que podem
divergir: `CommandLineFor` e `LabelFor` ([MainWindow.Nodes.cs:136-148](src/Orchestration.App/MainWindow.Nodes.cs)),
o sniffing de string em [TerminalNodeView.xaml.cs:69-72](src/Orchestration.App/Views/TerminalNodeView.xaml.cs)
(`command.Contains("claude")`), a validação de `HandleSpawn`
([MainWindow.Agent.cs:68](src/Orchestration.App/MainWindow.Agent.cs)) e o menu do XAML.

Criar `src/Orchestration.Core/Models/AgentKind.cs`: um registro estático com `Id`, `Label`, `Badge`,
`CommandLine` e `Glyph`, mais `AgentKind.All` e `AgentKind.Find(id)`. Glyph é só uma string de
codepoint — não é tipo de UI, então não fere a regra de Core sem UI.

Tipos: `powershell`, `claude`, `codex`, `gemini`, `cmd`. `gemini` reusa o mesmo padrão dos outros
(`powershell.exe -NoLogo -NoExit -Command gemini`), pelo motivo já documentado em `CommandLineFor`:
`CreateProcess` com application name nulo só acha `.exe`, e essas CLIs costumam ser `.ps1`/`.cmd`.

### F2. Campo `Kind` no modelo do terminal

`TerminalNode` ganha `public string Kind { get; set; } = "powershell"`. Isso substitui o sniffing
frágil de `CommandLine` e é o que permite ícone por tipo sem heurística.

Compatibilidade: `System.Text.Json` deixa o default quando o campo falta, e
`WorkspaceStore.Normalize` já é o lugar onde campos ausentes são preenchidos — backfill ali a partir
de `CommandLine` (mesma heurística de hoje, agora rodando **uma vez no load** em vez de a cada
render). Não precisa bump de `Workspace.Version`: adicionar campo com default é mudança compatível, e
`Normalize` roda para toda versão.

### F3. Borracha de arraste (rubber band) reutilizável

Um helper único que desenha um retângulo de preview durante o arraste e devolve o retângulo em
coordenadas de mundo ao soltar, distinguindo **clique** de **arraste** por um limiar (~12 px de tela).
Serve para: colocar terminal, colocar nota, retângulo, elipse, losango e seta. Sem ele, seis
implementações quase iguais.

## Item 1 — Clicar em Terminal deve perguntar o tipo

**Mudança:** trocar o `SplitButton` por um `Button` comum com `Flyout` anexado, de modo que
**qualquer** clique abra a lista. Isso também elimina o estado pegajoso `_shellKind`, que hoje faz o
botão criar silenciosamente um Claude só porque foi a última escolha — comportamento surpreendente.

O menu passa a ser gerado de `AgentKind.All` (F1), cada item com seu ícone (item 4). `OnPickShell`
deixa de criar direto: passa a **armar o modo de colocação** (item 2) com o tipo escolhido.

Arquivos: `MainWindow.xaml` (54-67), `MainWindow.Nodes.cs` (`OnNewTerminalClicked`, `OnPickShell`,
`_shellKind`, `CommandLineFor`, `LabelFor`).

## Item 2 — Cursor armado: clique = tamanho padrão, arraste = tamanho desenhado

**Estado atual:** `OnNewTerminal`/`OnNewNote` criam imediatamente em `NextSpawnPoint()`, uma posição
escalonada — o usuário não escolhe onde nem o tamanho.

**Mudança:** dois novos modos na máquina de ferramentas já existente: `place-terminal` (com o tipo
escolhido guardado) e `place-note`.

- Clique sem arraste → tamanho padrão atual (terminal 720×420, nota 340×240).
- Arraste → tamanho do retângulo em coordenadas de mundo, com piso nos mínimos que
  `RegisterResize` já usa ([MainWindow.Wires.cs:232-233](src/Orchestration.App/MainWindow.Wires.cs)):
  terminal 240×160, nota 160×100.
- `Esc` cancela; após criar, volta para a ferramenta `select` (uma criação por armação, como Excalidraw).

**Validar antes de armar, não depois.** `CreateNote` recusa sem projeto aberto
([MainWindow.Nodes.cs:369-373](src/Orchestration.App/MainWindow.Nodes.cs)); se essa checagem ficar no
fim, o usuário arrasta um retângulo e só então recebe o erro. A checagem sobe para o momento do clique
no botão Nota.

**Cursor de mira:** `UIElement.ProtectedCursor` é protegido no WinUI 3, então não dá para setar num
`Grid` declarado em XAML. Custo honesto: um arquivo mínimo `Views/CursorGrid.cs` (`class CursorGrid :
Grid` expondo um setter de cursor) e trocar o `Viewport` para esse tipo. Vale o arquivo — sem
feedback de cursor o modo armado fica invisível e a sensação "tipo Excalidraw" se perde.

`NextSpawnPoint` continua existindo: `HandleSpawn` (filhos criados por agente) e o primeiro terminal
de `RestoreWorkspace` ainda usam posicionamento automático.

Modo de colocação e `ConnectionModeToggle` são mutuamente exclusivos, e enquanto armado o pan de
fundo fica desligado (`OnCanvasPointerPressed` já dá precedência a `TryStartCanvasTool`).

## Item 3 — Texto grande/pequeno e setas

**Texto:** o tamanho já existe, mas só para o próximo texto. A lacuna real é **não poder mexer no que
já está no canvas**. Mudança: com a ferramenta `select`, clicar numa anotação a seleciona; a régua
contextual (`CanvasContextBar`, que já troca de conteúdo por ferramenta) passa a editar cor e tamanho
do item selecionado. Isso reusa `CanvasSizeUp`/`CanvasSizeDown`/`OnCanvasColor`, que hoje só mexem em
estado global.

**Setas:** novo `CanvasItemKind.Arrow`. O modelo **não precisa de campo novo** — `CanvasItem.Points`
já é uma lista, e uma seta é `Points[0]` → `Points[^1]`. Render: um `Path` com haste e uma ponta
triangular, ou `Polyline` + `Polygon`. Criação via F3 (arrastar de um ponto ao outro).

Espessura segue a regra que `PositionAnnotation` já usa (`Math.Max(1, item.Size * _zoom)`), então
setas não desaparecem no zoom-out.

## Item 4 — Formas geométricas e ícone por tipo de terminal

**Formas:** `CanvasItemKind.Rectangle`, `Ellipse`, `Diamond` (losango = decisão, o suficiente para
fluxos). Como na seta, `Points` guarda dois cantos — **zero campo novo no modelo**, logo zero risco de
persistência. Render com `Shapes.Rectangle`/`Ellipse`/`Path` posicionados por `Canvas.Left/Top` +
`Width/Height` derivados dos dois pontos × zoom. Contorno sem preenchimento (o preenchimento
esconderia a grade e brigaria com a paleta restrita).

**Ícones por tipo:** com `Kind` no modelo (F2) e o glyph na tabela (F1), o cabeçalho do nó mostra um
`FontIcon` ao lado do badge que já existe (`KindText`). Glyphs distintos do Segoe Fluent Icons —
`E756` (prompt) para shell, `E945`/`E943`/`E734` para os agentes, ajustados na implementação por
legibilidade a 12 px.

Decisão explícita: **o ícone diferencia, a cor não.** `DESIGN.md` reserva as cores para semântica de
execução/erro, e o header já tem `AccentColor` escolhido pelo usuário. Cor por tipo de agente
colidiria com as duas coisas.

`SetCanvasTool` hoje liga/desliga quatro `ToggleButton` nomeados à mão
([MainWindow.Tools.cs:31-39](src/Orchestration.App/MainWindow.Tools.cs)). Com ~9 ferramentas isso
vira uma lista de erro fácil: passa a iterar os filhos da régua comparando `Tag`.

## Item 5 — Zoom e tamanho do canvas

Aqui há um **bug real**, não só um limite apertado.

**Achado:** `MainWindow.Canvas.cs:16` define `MinZoom = 0.5, MaxZoom = 2.5`, mas
`Core/Models/Camera.cs` define `MinZoom = 0.3`. Os dois discordam: `WorkspaceStore.Normalize` clampa
pelo valor do `Camera` (0.3), então um workspace salvo pode carregar em 0.3 — um zoom que a interface
não consegue nem alcançar nem reproduzir. Correção: **uma fonte de verdade**, `Camera`, consumida por
`Canvas.cs`.

**Sobre "o canvas acaba muito rápido":** o canvas já é infinito por construção — posição é
`X * zoom + offset`, sem extensão máxima. O que "acaba" é a faixa de zoom. Com passo 1.1 por notch,
de 100% até o piso de 50% são só ~7 notches. Novos limites: **0.1 a 4.0**.

Três consequências que precisam ser tratadas junto, ou o zoom-out largo fica pior que o limite atual:

1. **Grade explode.** `DrawGrid` usa `spacing = 40 * _zoom`; em zoom 0.1 isso dá 4 px, ou seja
   milhares de `Line` recriadas a cada movimento de pan. Correção: passo adaptativo — multiplicar por
   5 enquanto o espaçamento na tela for menor que ~24 px (e dividir se passar de ~160 px), mantendo
   linha principal a cada 5 células. Isso limita a grade a algumas dezenas de linhas em qualquer zoom
   e resolve desempenho e poluição visual de uma vez.

2. **Terminal fica inútil mas continua caro.** A fonte tem piso de 12 px
   ([TerminalNodeView.xaml.cs:234](src/Orchestration.App/Views/TerminalNodeView.xaml.cs)), correto por
   design — mas em zoom 0.1 a caixa do nó tem 72 px de largura com fonte de 12 px, o que faz o addon
   `fit` calcular pouquíssimas colunas e disparar `Resize` do ConPTY para um tamanho absurdo. Correção:
   abaixo de um limiar (~0.4), trocar o `WebView2` por um cartão colapsado com título, badge, ícone e
   ponto de estado. Ganho duplo: legibilidade e desempenho (é a ideia de "colapsar nós" do `PLANO.md`,
   aplicada ao zoom em vez da viewport). Vale a mesma coisa para nota.

3. **Cabos ficam grossos demais.** `MainWindow.Wires.cs` usa espessura fixa 4/5 px; em zoom baixo
   dominam a tela. Passam a escalar com o zoom com piso de ~1,5 px.

**Bônus coerente:** o botão de reset tem tooltip "Ajustar zoom"
([MainWindow.xaml:98](src/Orchestration.App/MainWindow.xaml)) mas `OnResetView` só volta para 100% na
origem — o que, num canvas agora muito mais largo, "perde" nós distantes e alimenta exatamente a
sensação de canvas quebrado. Passa a fazer o que promete: enquadrar todos os nós e anotações com
margem, caindo para 100%/origem quando o canvas está vazio.

## Ordem de implementação

1. **F1 + F2** (tabela de tipos, campo `Kind`, backfill no `Normalize`) — base dos itens 1 e 4, e a
   única parte que toca persistência. Teste de round-trip e de backfill de arquivo antigo.
2. **Item 5** — independente e é o que mais incomoda hoje; entregar cedo. Inclui unificar os limites,
   grade adaptativa, colapso por zoom, espessura de cabo e enquadrar-para-caber.
3. **F3** (rubber band) + **Item 2** — colocação por clique/arraste para terminal e nota.
4. **Item 1** — o menu passa a armar a colocação (depende de F1 e do item 2).
5. **Item 4 formas** + **Item 3 setas** — mesma mecânica de F3, entram juntas.
6. **Item 3 seleção de anotação** — editar tamanho/cor do que já existe; é o que tem mais superfície
   de UI e menos risco, então fecha por último.

## Verificação

- `dotnet test` continua verde (71 testes hoje) mais os novos: round-trip de `Kind`, backfill de
  workspace sem `Kind`, round-trip de `Arrow`/`Rectangle`/`Ellipse`/`Diamond`, e a matemática de
  `Camera` clamp com os novos limites.
- Grade: em zoom 0.1 e 4.0, contar os `Line` gerados (deve ficar em dezenas, não milhares) e conferir
  que o pan continua fluido.
- Terminal: com um `claude` rodando, dar zoom-out abaixo do limiar e voltar — o processo tem de
  continuar vivo e o conteúdo reaparecer sem lixo (o colapso troca a apresentação, não a sessão).
- Colocação: clique cria tamanho padrão; arraste pequeno (< limiar) também; arraste grande respeita o
  tamanho; `Esc` cancela sem criar nada.
- Persistência: criar um de cada tipo de forma, fechar e reabrir o app — tudo volta na posição e
  tamanho certos.
- Nota sem projeto aberto: o aviso aparece **no clique do botão**, antes de qualquer arraste.

## Fora de escopo (e o que dispararia)

- **Redimensionar e multi-selecionar anotações** com alças. O item 3/4 entrega criar, mover, apagar e
  editar tamanho/cor — o suficiente para montar fluxos. Alças de redimensionamento entram quando
  editar um fluxo já feito virar incômodo real.
- **Undo/redo.** Já estava listado como pós-MVP no `PLANO.md` e cresce em importância agora que há
  mais coisas para criar por engano. Não entra aqui, mas sobe de prioridade.
- **Ícones de marca** (logos reais de Claude/Codex/Gemini). Glyphs do Fluent evitam questão de licença
  e de tema claro/escuro; assets de marca entram se a diferenciação por glyph não bastar na prática.
- **Snap à grade** ao colocar/mover. A grade de 40 px já existe e o snap seria natural, mas não foi
  pedido; deixar de fora até alguém reclamar de alinhamento.
