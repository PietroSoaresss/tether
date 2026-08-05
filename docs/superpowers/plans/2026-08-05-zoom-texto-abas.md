# Plano — Zoom que escala de verdade, ferramentas de texto completas e abas de canvas

Data: 2026-08-05

## Contexto

Três pedidos: (1) corrigir o zoom — terminais e notas não acompanham a escala, letras ficam
desproporcionais; (2) ferramentas de texto completas no estilo Excalidraw; (3) várias abas de canvas.

**Achado que vem antes de tudo: `master` não compila.** O commit `8b1f2a1 fix: canvas zoom` entregou o
código C# do plano anterior sem o XAML correspondente. `dotnet build src/Orchestration.App` falha com:

| Erro | Onde | Causa |
|---|---|---|
| CS0103 `Camera` não existe no contexto | [MainWindow.Canvas.cs:17](src/Orchestration.App/MainWindow.Canvas.cs) | falta `using Orchestration.Core.Models;` |
| CS0111 `UpdateRubber` duplicado | [MainWindow.Wires.cs:104](src/Orchestration.App/MainWindow.Wires.cs) vs [MainWindow.Tools.cs:294](src/Orchestration.App/MainWindow.Tools.cs) | dois membros `UpdateRubber(Point)` na mesma partial |
| (bloqueado pelos acima) `CanvasToolRuler` não existe | [MainWindow.Tools.cs:55](src/Orchestration.App/MainWindow.Tools.cs) | a régua no XAML não tem `x:Name` nem os botões de forma |

Nada mais é verificável enquanto isso não fechar. É a fase 0.

---

## Fase 0 — Voltar a compilar

1. `using Orchestration.Core.Models;` em `MainWindow.Canvas.cs`.
2. Renomear o `UpdateRubber` de `Wires.cs` para `UpdateRubberWire` (é o cabo; o de `Tools.cs` é o
   retângulo de seleção — nomes diferentes porque são coisas diferentes).
3. `MainWindow.xaml`: dar `x:Name="CanvasToolRuler"` ao `StackPanel` da régua e acrescentar os quatro
   `ToggleButton` que o C# já espera — `arrow`, `rect`, `ellipse`, `diamond` (atalhos A/R/O/D já estão
   em `HandleCanvasToolShortcut`).

Critério: `dotnet build` limpo e `dotnet test` verde. Sem isso, nenhuma fase seguinte é testável.

---

## Fase 1 — Zoom

### Diagnóstico

O zoom é assado na geometria do nó (`PlaceNode`: `Width = node.Width * _zoom`), o que está certo — o
`WebView2` não é composto pelo XAML. O que quebra são duas coisas independentes:

**(a) O clamp de fonte desalinha o terminal do seu próprio box.**
`TerminalNodeView.ApplyZoom` faz `Math.Clamp(_baseFontSize * zoom, 12, 48)`. Enquanto a fonte escala
linearmente, o número de colunas é constante: `cols = (largura_mundo × zoom) / (largura_char × zoom)`.
No momento em que o clamp engata, `zoom` sai do numerador e não do denominador — as colunas passam a
variar com o zoom, o `FitAddon` recalcula e o **ConPTY é redimensionado**. O shell reflui a saída a cada
notch. Com base 14 px, o piso engata abaixo de `zoom 0.86` e o teto acima de `3.43` — ou seja, a maior
parte da faixa `0.1–4.0`. É por isso que "as letras ficam pequenas": acima de 3.43 o box continua
crescendo e a fonte não. `NoteNodeView.ApplyZoom` tem o mesmo clamp.

**(b) O cabeçalho não escala.** `HeaderRow.Height="44"`, `Padding="12,0"`, ícones 12–13 px, badge 9 px,
título 12 px, subtítulo 10 px — tudo em pixels de tela, fixo. Em `zoom 0.5` o cabeçalho ocupa o dobro da
proporção; em `zoom 2` vira uma fita. Vale para `TerminalNodeView.xaml` e `NoteNodeView.xaml`.

### Correção (a) — trocar o piso de fonte pelo cartão colapsado

`DESIGN.md:49` diz "terminal e notas nunca ficam menores que 12 px durante o zoom" e `PRODUCT.md`
princípio 5 diz "zoom reduz geometria, nunca torna terminal ou nota ilegível" (a seção de
acessibilidade fala em 10 px — os dois documentos já divergem). **A regra do piso é a causa do bug e
não é o único jeito de cumprir o princípio 5.** O cartão colapsado (`CollapseZoom`, já implementado)
cumpre melhor: um cartão com título e badge é legível; um terminal de 6 colunas com fonte travada em
12 px não é.

Mudança:

- `ApplyZoom` passa a usar `Math.Max(_baseFontSize * zoom, 10)` — sem teto, e com o piso em 10 px
  (o valor da seção de acessibilidade do `PRODUCT.md`).
- `CollapseZoom` sobe de `0.4` para `0.7`, o ponto onde a fonte base de 14 px chega em ~10 px. Assim
  **o piso nunca chega a engatar com o terminal vivo**: acima de 0.7 a escala é exatamente linear e as
  colunas ficam constantes; abaixo, é cartão. A nota (base 13) fica 2% fora — invisível.
- Atualizar `DESIGN.md:49` para descrever o mecanismo real: "abaixo de 70% o nó vira cartão; acima, a
  tipografia acompanha a escala linearmente".

Consequência visível a aceitar: nós colapsam mais cedo do que hoje (0.7 em vez de 0.4). É uma troca
deliberada — no intervalo 0.4–0.7 o terminal atual já é ilegível e ainda paga o custo do `WebView2`.
Se incomodar, o número é um só e fica em `MainWindow.Canvas.cs`.

`index.html` **não muda**. A mecânica de escala já estava certa; só o clamp a estragava.

### Correção (b) — escalar o chrome com um único transform

Em cada view, o conteúdo do cabeçalho passa a viver num `Grid` interno com `ScaleTransform`:

```
HeaderBar (externo, altura da linha = 44 × zoom, recorta)
└── HeaderContent (Height=44, Width = ActualWidth / zoom, RenderTransform=Scale(zoom))
    └── conteúdo atual, sem tocar em nenhum FontSize
```

`RenderTransform` não afeta layout, então o cabeçalho é medido a 1× e pintado escalado — um transform
no lugar de ~12 atribuições de `FontSize`, e o hit-testing dos botões continua correto. A largura vem
de um handler `SizeChanged` no próprio `NodeRoot` (`HeaderContent.Width = ActualWidth / _lastZoom`),
o que também acerta o caso de arrastar o grip de resize. Sem mudar `INodeView`.

**Armadilha a tratar:** `SetCollapsed` escreve `HeaderRow.Height = Star` e sai cedo quando o estado não
mudou. `ApplyLayout` chama `ApplyZoom` **antes** de `SetCollapsed`, então um zoom de 0.3 → 0.2 (ambos
colapsados) faria `ApplyZoom` devolver a altura para `44 × zoom` e `SetCollapsed` não corrigiria.
`ApplyZoom` precisa respeitar `_collapsed`.

Ficam fixos de propósito: raio de canto (12 px), espessura de borda (1 px) e o grip de resize (20 px).
Alças que não escalam é o comportamento do Excalidraw e é o que mantém o alvo clicável em zoom baixo.

### Verificação

- Em `zoom 1.0`, rodar `mode con` (ou `tput cols`) no terminal; variar o zoom de 0.7 a 4.0 e repetir:
  **as colunas têm de ser as mesmas**. Hoje mudam.
- Zoom 4.0: a proporção cabeçalho/corpo tem de ser a mesma de 100%.
- Abaixo de 0.7: cartão; voltar acima: o processo continua vivo e o conteúdo reaparece sem lixo.

---

## Fase 2 — Ferramentas de texto completas

Depois da fase 0 a régua tem as 9 ferramentas. Falta o que o Excalidraw tem para **texto**:

| Falta hoje | Mudança |
|---|---|
| Não dá para **mover** um texto | `OnAnnotationPressed` retorna cedo para `Text` ([MainWindow.Tools.cs:574](src/Orchestration.App/MainWindow.Tools.cs)) porque a `TextBox` está sempre editável. Passa a nascer `IsReadOnly` — clique seleciona e arrasta, duplo clique libera a edição e foca, perder o foco volta a travar. |
| Família de fonte | `CanvasItem.Font` (`"ui"` \| `"mono"` \| `"serif"`), padrão `ui`. Três botões na barra de contexto. |
| Negrito / itálico | `CanvasItem.Bold`, `CanvasItem.Italic` (bool). Dois `ToggleButton`. |
| Alinhamento | `CanvasItem.Align` (`"left"` \| `"center"` \| `"right"`), padrão `left`. |
| Texto vazio vira item invisível | Ao sair da edição com texto em branco, remover o item. |

Todos os campos são **aditivos com default**, então arquivo antigo carrega sem mudança e não há bump de
`Workspace.Version` — mesmo argumento já usado para `TerminalNode.Kind`. `WorkspaceStore.Normalize`
ganha `item.Font ??= "ui"` junto dos `??=` que já estão lá.

A barra de contexto (`CanvasContextBar`) já troca de conteúdo por ferramenta e já aplica cor/tamanho ao
item selecionado — os controles novos entram no mesmo `UpdateCanvasToolContext`, visíveis quando a
ferramenta é `text` ou o item selecionado é `Text`.

### Verificação

Criar texto, mudar família/peso/alinhamento, mover, fechar e reabrir o app: tudo volta igual. Abrir um
`workspace.json` anterior a esta fase: os textos carregam com os defaults.

---

## Fase 3 — Abas de canvas

É a fase que toca persistência, então vem por último.

### Modelo

```
CanvasTab { Guid Id, string Name, Camera Camera,
            List<NodeBase> Nodes, List<Connection> Connections, List<CanvasItem> CanvasItems }

Workspace { int Version, string ProjectDirectory, List<CanvasTab> Tabs, Guid ActiveTabId }
```

As listas de topo (`Nodes`, `Connections`, `CanvasItems`, `Camera`) continuam existindo como campos
somente-leitura de migração. `Migrate` ganha o braço `case 1:` — que já está lá esperando exatamente
isso — e dobra o conteúdo de topo num único `CanvasTab` chamado "Canvas 1", esvaziando os campos
antigos. `Workspace.CurrentVersion` vai para 2.

### App

`MainWindow` ganha `_canvas` (a aba ativa) e as ~15 referências a `_workspace.Nodes` /
`.Connections` / `.CanvasItems` / `.Camera` passam a apontar para ela. É mecânico.

**Sessões sobrevivem à troca de aba** — é o ponto do produto: um agente na aba 2 continua trabalhando
enquanto você olha a aba 1. Portanto trocar de aba **não** destrói views:

- Todas as abas são materializadas no load; cada `CanvasNode` carrega o `TabId`.
- Trocar aba = `Visibility` nos nós + restaurar a `Camera` daquela aba. `PlaceNode`, `ApplyLayout`,
  `RenderWires`, `RenderAnnotations`, `TryContentBounds` e os hit-tests filtram pela aba ativa.
- Esconder `WebView2` já é mecanismo provado — é o que `SetCollapsed` faz hoje.
- Fechar uma aba, aí sim, descarta as sessões dela (com confirmação se tiver nós).

Custo honesto: todos os terminais de todas as abas sobem no launch. É o mesmo custo de ter os mesmos
terminais numa aba só, e é o comportamento que o produto promete.

`Connection` é plana (source/target por `Guid`). Cabo entre abas não é criável pela UI (o alvo não está
visível) e um cabo órfão vindo de arquivo editado à mão é ignorado no render — sem campo novo.

### UI

Faixa de abas no topo do `Viewport`: clique troca, `+` cria, duplo clique renomeia in-place, `×` fecha
a ativa. Segue a paleta existente — lime marca a aba ativa, como já marca o projeto ativo na sidebar.

### Verificação

- Round-trip: duas abas com nós, formas e câmeras diferentes; fechar e reabrir; cada aba volta na sua
  posição e no seu zoom.
- Migração: `workspace.json` da versão 1 abre como uma aba "Canvas 1" com tudo no lugar.
- Sessão: iniciar um `claude` na aba 2, trocar para a 1, esperar, voltar — o processo continua vivo e a
  saída produzida no intervalo está lá.
- Trocar de projeto continua limpando tudo.

---

## Ordem e razão

| # | Fase | Por quê nessa posição |
|---|---|---|
| 0 | Compilar | Nada é verificável antes |
| 1 | Zoom | Independente, é a queixa mais forte, e a maior parte é remover código |
| 2 | Texto | Depende só da régua reparada na fase 0 |
| 3 | Abas | Única que mexe em persistência; maior raio de dano; precisa das outras estáveis para ser testada |

Testes novos em `tests/Orchestration.Core.Tests`: round-trip de `CanvasTab`, migração de arquivo v1
para abas, round-trip dos campos de estilo de texto.

## Fora de escopo (e o que dispararia cada um)

- **Undo/redo.** Já era pós-MVP no `PLANO.md` e sobe de prioridade de novo com abas e mais ferramentas.
  Entra quando alguém perder trabalho de verdade.
- **Arrastar para reordenar abas, cor por aba, mover nó entre abas.** Entram quando passar de ~5 abas.
- **Texto dentro de forma / rótulo em seta.** Excalidraw tem; é outra estrutura no modelo (item filho).
  Entra se anotar fluxo com forma+texto solto virar incômodo.
- **Snap à grade e alças de redimensionar anotação.** Continuam fora, pelos mesmos motivos do plano de
  2026-07-29.
- **Escalar raio, borda e grip com o zoom.** Fixos de propósito (ver fase 1).
