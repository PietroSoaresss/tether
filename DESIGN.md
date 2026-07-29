---
version: 1
name: Tether Visual System
description: Sistema de produto inspirado na linguagem violeta do Sentry, adaptado a um canvas técnico de agentes. Violeta estrutura, lime destaca atividade e vermelho comunica erro.
colors:
  canvas-dark: "#1F1633"
  night: "#150F23"
  surface-dark: "#241A3B"
  surface-raised-dark: "#2A2042"
  border-dark: "#362D59"
  canvas-light: "#FFFFFF"
  surface-light: "#F7F6FA"
  surface-raised-light: "#FFFFFF"
  border-light: "#D8D4E2"
  text-on-dark: "#FFFFFF"
  text-secondary-dark: "#C9C4D1"
  text-muted-dark: "#9E97AA"
  text-on-light: "#1F1633"
  text-secondary-light: "#554C68"
  violet: "#79628C"
  lime: "#C2EF4E"
  red: "#FF5C5C"
  red-pressed: "#D83A3A"
rounded:
  badge: 4
  control: 8
  node: 12
spacing:
  xxs: 2
  xs: 4
  sm: 8
  md: 12
  lg: 16
  xl: 24
---

# Tether Visual System

## Scene and strategy

Um desenvolvedor coordena vários agentes por horas, frequentemente num escritório de baixa luz, e precisa reconhecer execução, erro e autorização sem brilho excessivo. O canvas é escuro por padrão e usa uma estratégia restrita: violeta ocupa as superfícies, enquanto lime e vermelho aparecem somente como semântica.

## Typography

- Interface: `Segoe UI Variable Text`, 12–14 px, pesos 400–600.
- Marca e títulos compactos: `Segoe UI Variable Display`, 14–16 px, peso 600.
- Terminal e Markdown bruto: `Cascadia Mono, Consolas`, configurável.
- Preview Markdown: família da interface.
- Terminal e notas nunca ficam menores que 12 px durante o zoom.

## Surfaces and depth

Profundidade por mudança de superfície e bordas de 1 px. Não usar sombras decorativas no canvas escuro. A barra superior usa `night`; o canvas usa `canvas-dark`; nós usam `surface-dark`; cabeçalhos usam `surface-raised-dark`.

No tema claro, a mesma hierarquia usa branco, `surface-light` e bordas violetas dessaturadas. O terminal interno permanece escuro nos dois temas.

## Layout

- Barra superior: 56 px, grupos esquerdo/centro/direito e base de 8 px.
- Projetos recentes: 232 px à esquerda, busca, agrupamento por pasta-pai e seleção lime.
- Canvas infinito: grade de 40 px; linha principal a cada cinco células.
- Explorador do projeto: 288 px à direita, recolhível e separado por uma borda.
- Cabeçalho do nó: 44 px, status, badge, título, projeto e ações.
- Raio dos nós: 12 px. Controles: 8 px. Badges: 4 px.
- Sem grip visível; o canto inferior direito conserva cursor de resize.

## Components

### Project bar

`TETHER` ancora a esquerda. O seletor do projeto é a ação primária; terminal e nota são ações secundárias. Conectar, arquivos, zoom, ajustar visão e configurações ficam agrupados à direita. Instruções permanentes saem da barra e viram tooltips.

### Nodes

Terminal e nota compartilham o mesmo chrome. Badges identificam `CLAUDE`, `CODEX`, `SHELL` e `NOTA`. Seleção usa uma borda lime de 1,5 px; hover usa borda violeta mais clara. Ações destrutivas ficam vermelhas apenas em hover/pressed.

### Terminal states

- Iniciando/parado: violeta.
- Executando: lime.
- Encerrado/erro: vermelho `#FF5C5C`, acompanhado de texto e ação de reinício.

### Notes

Markdown bruto usa monoespaçada; preview usa a fonte da interface. Cada nota é um arquivo real em `<projeto>/notes/*.md`; o workspace guarda apenas o vínculo. Arquivo ausente apresenta ícone, mensagem e botão de recriar, com vermelho semântico.

### Wires

Cabos em repouso usam violeta, 4 px. Cabo selecionado ou sendo criado usa lime, 5 px. Não há portas fixas: no modo Conectar, toda a superfície dos nós aceita início e destino, e as âncoras escolhidas ficam persistidas. O mesmo par direcionado não recebe cabos duplicados.

### Project explorer

A árvore usa o projeto ativo persistido, o mesmo fundo do canvas e profundidade por borda. Pastas carregam sob demanda, aparecem antes dos arquivos e acompanham criação, remoção e renomeação. Backups e temporários internos não aparecem.

### Recent projects

A sidebar esquerda funciona como uma estante de canvases. Projetos abertos ficam agrupados pela pasta-pai, podem ser filtrados e exibem uma marca lime somente no projeto ativo. O botão `+` abre outra pasta; selecionar uma linha salva o canvas atual e carrega o estado de `<projeto>/.tether/workspace.json`.

### Canvas sketch layer

O domínio combina quadro branco, mapa de agentes, terminal, caderno técnico e diagrama de fluxo. A camada de rascunho fica atrás de nós e cabos e compartilha as mesmas coordenadas do canvas. Seu mundo de cor usa grafite/violeta nas superfícies, branco suave nos traços, violeta claro nas guias, lime na seleção e vermelho apenas para apagar.

A assinatura é a conversão direta de um texto desenhado em nota Markdown do projeto. Isso substitui três padrões genéricos: barra lateral de formas por uma régua flutuante compacta; painel permanente de propriedades por controles contextuais; alças sempre visíveis por contorno e cursores somente durante a seleção.

- Ferramentas MVP: selecionar, mão, texto, lápis, retângulo, elipse, seta e borracha.
- Atalhos: `V`, `H`, `T`, `P`, `R`, `O`, `A`, `E`; `Espaço` ativa a mão temporariamente.
- Texto edita inline com duplo clique. `Criar nota` salva o conteúdo em `<projeto>/notes/*.md`.
- Traços usam largura e opacidade discretas; sem paleta livre no MVP.
- Seleção múltipla, mover, redimensionar, duplicar, apagar e undo/redo ficam disponíveis.
- Zoom altera a geometria, mas mantém texto editável legível enquanto está em edição.

As ferramentas ficam em uma régua flutuante central: selecionar, criar terminal, criar nota, texto, lápis e borracha. Uma segunda régua contextual oferece paleta e tamanho; ao selecionar um terminal, a mesma paleta tinge seu cabeçalho e borda sem alterar as cores semânticas de execução e erro.

### Controls and dialogs

Controles têm estados default, hover, focus, pressed e disabled. O foco usa lime com contraste suficiente. Configurações são agrupadas em Geral, Terminal, Agentes e Atalhos dentro do `ContentDialog` padrão.

## Motion

Transições de cor/opacidade entre 150–180 ms, sem bounce. Movimento comunica seleção, criação de cabo ou mudança de estado. Com movimento reduzido, a mudança é instantânea.

## Guardrails

- Um foco lime por região visível.
- Vermelho somente para erro, encerramento e destruição.
- Sem rosa, laranja, gradientes, glassmorphism ou mascotes.
- Sem sombras largas, cards aninhados ou texto decorativo em caixa alta.
- Toda cor semântica deve ter reforço textual, icônico ou geométrico.
