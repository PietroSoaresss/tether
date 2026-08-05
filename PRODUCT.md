# Product

## Register

product

## Users

Desenvolvedores que coordenam agentes de IA locais, shells e notas durante sessões longas de trabalho. Eles precisam abrir um projeto, enxergar quem pode conversar com quem e delegar tarefas sem perder o contexto da pasta ou sair do fluxo.

## Product Purpose

Tether é um canvas local de orquestração. Cada projeto pode conter terminais reais, agentes Claude/Codex e notas Markdown salvas como arquivos do próprio projeto; cabos visuais autorizam comunicação entre nós, e a CLI `tether` permite delegar, consultar notas e criar novos agentes. Sucesso significa que o grafo explica o sistema num olhar e desaparece enquanto o usuário trabalha.

Pastas já abertas permanecem na sidebar esquerda. Cada pasta possui seu próprio canvas persistido em `.tether/workspace.json`, enquanto notas continuam como arquivos Markdown reais em `notes/`.

O canvas também aceita textos e traços vetoriais persistidos. Ferramentas flutuantes permitem anotar o grafo e usar cores discretas para diferenciar terminais, sem confundir essa identidade com os estados de execução e erro.

## Brand Personality

Técnico, confiante e simples. A personalidade tem a energia irreverente do Sentry, mas com contenção de ferramenta profissional: precisa, direta e sem decoração competindo com o trabalho.

## Anti-references

- Chrome cinza genérico do WinUI sem identidade.
- Dashboards SaaS em bege, glassmorphism, gradientes e sombras largas.
- Neon espalhado, muitos acentos concorrentes ou aparência gamer.
- Mascotes e elementos de marketing dentro da área de trabalho.
- Texto pequeno, conexões difíceis de acertar e affordances escondidas.
- Rosa ou laranja para erro; o sistema usa vermelho semântico.

## Design Principles

1. O grafo vem antes do chrome: nós, cabos e estado devem dominar a leitura.
2. Projeto é contexto: a pasta ativa precisa estar sempre visível e ser herdada por agentes-filhos.
3. Cor comunica: violeta estrutura, lime indica atividade/seleção e vermelho indica erro/destruição.
4. Familiaridade ganha: controles padrão, atalhos, foco e estados previsíveis.
5. Legibilidade não encolhe: zoom reduz geometria, nunca torna terminal ou nota ilegível.

## Accessibility & Inclusion

Alvo WCAG AA para contraste. Todo estado colorido também deve ter texto, ícone ou forma; foco de teclado permanece visível; terminal e notas nunca são exibidos em tamanho ilegível — abaixo de 40% de zoom o nó vira cartão em vez de encolher a tipografia; controles principais têm alvos confortáveis; animações são curtas, funcionais e dispensáveis quando movimento reduzido estiver ativo.
