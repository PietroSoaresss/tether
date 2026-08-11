using Orchestration.App.Services;
using Orchestration.App.Views;
using Orchestration.Core.Graph;
using Orchestration.Core.Ipc;
using Orchestration.Core.Models;
using Orchestration.Core.Terminal;

namespace Orchestration.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<Guid, TerminalNodeView> _terminalViews = new();
    private readonly Dictionary<Guid, SemaphoreSlim> _askGates = new();
    private readonly Dictionary<Guid, int> _askQueues = new();
    private readonly Dictionary<Guid, IReadOnlyList<Guid>> _activeChains = new();

    private Task<TetherResponse> HandleTetherRequest(TetherRequest request)
    {
        if (DispatcherQueue.HasThreadAccess) return HandleTetherRequestOnUi(request);

        var completion = new TaskCompletionSource<TetherResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try { completion.SetResult(await HandleTetherRequestOnUi(request)); }
            catch (Exception e) { completion.SetResult(TetherResponse.Failure(e.Message)); }
        }))
            completion.SetResult(TetherResponse.Failure("app is closing"));
        return completion.Task;
    }

    private async Task<TetherResponse> HandleTetherRequestOnUi(TetherRequest request)
    {
        if (AllNodes().All(node => node.Id != request.From))
            return TetherResponse.Failure("unknown caller");

        switch (request.Cmd)
        {
            case "list":
                return TetherResponse.Success(string.Join(
                    Environment.NewLine,
                    Authorization.Neighbors(TabOf(request.From), request.From)
                        .Select(node => $"{node.Id}  {NodeKindLabel(node)}  {node.Title}")));

            case "note-show":
            case "note-edit":
                return HandleNoteRequest(request);

            case "ask":
                return await HandleAsk(request);

            case "spawn":
                return HandleSpawn(request);

            default:
                return TetherResponse.Failure("unknown command");
        }
    }

    private static string NodeKindLabel(NodeBase node) =>
        node switch { TerminalNode => "terminal", BrowserNode => "browser", _ => "note" };

    private TetherResponse HandleSpawn(TetherRequest request)
    {
        if (AllNodes().FirstOrDefault(node => node.Id == request.From) is not TerminalNode caller)
            return TetherResponse.Failure("caller is not a terminal");

        string kind = request.Args.GetValueOrDefault("kind", "").ToLowerInvariant();
        string prompt = request.Args.GetValueOrDefault("prompt", "");
        string requestedTitle = request.Args.GetValueOrDefault("title", "").Trim();
        if (!AgentKind.CanSpawn(kind) || string.IsNullOrWhiteSpace(prompt))
            return TetherResponse.Failure("invalid kind or prompt");
        var agent = AgentKind.Find(kind);
        if (requestedTitle.Length > 80)
            return TetherResponse.Failure("title too long");
        if (requestedTitle.Length > 0 && AllNodes().Any(node =>
                string.Equals(node.Title, requestedTitle, StringComparison.OrdinalIgnoreCase)))
            return TetherResponse.Failure("title already exists");

        // A spawned child belongs on the caller's canvas, which is not necessarily the one on screen.
        var canvas = TabOf(caller.Id);
        var child = new TerminalNode
        {
            Title = requestedTitle.Length > 0
                ? requestedTitle
                : $"{agent.Label} {Guid.NewGuid():N}"[..12],
            X = caller.X + caller.Width + 80,
            Y = caller.Y + canvas.Connections.Count(connection => connection.SourceId == caller.Id) * 48,
            Width = 720,
            Height = 420,
            Kind = agent.Id,
            CommandLine = agent.CommandLine,
            WorkingDirectory = string.IsNullOrWhiteSpace(caller.WorkingDirectory)
                ? _workingDirectory
                : caller.WorkingDirectory
        };
        canvas.Connections.Add(new Connection
        {
            SourceId = caller.Id,
            TargetId = child.Id,
            Bidirectional = true
        });
        Materialize(child, prompt, canvas);
        RenderWires();
        _autosave.Touch();
        return TetherResponse.Success($"{child.Id}  terminal  {child.Title}");
    }

    private TetherResponse HandleNoteRequest(TetherRequest request)
    {
        if (!request.Args.TryGetValue("target", out string? targetText))
            return TetherResponse.Failure("missing target");
        var target = Authorization.Resolve(TabOf(request.From), targetText);
        if (target is not NoteNode note)
            return TetherResponse.Failure("note not found or ambiguous");
        if (!Authorization.CanAccess(TabOf(request.From), request.From, note.Id))
            return TetherResponse.Failure("no route");
        string? folder = NoteFolder(note);
        if (!_noteFiles.Exists(note.FileName, folder))
            return TetherResponse.Failure("note file missing");

        if (request.Cmd == "note-show")
            return TetherResponse.Success(_noteFiles.Read(note.FileName, folder));

        string content = request.Args.GetValueOrDefault("content", "");
        if (request.Args.GetValueOrDefault("append") == "true")
            content = _noteFiles.Read(note.FileName, folder) + content;
        _noteFiles.Write(note.FileName, content, folder);
        return TetherResponse.Success();
    }

    private async Task<TetherResponse> HandleAsk(TetherRequest request)
    {
        if (!request.Args.TryGetValue("target", out string? targetText) ||
            !request.Args.TryGetValue("prompt", out string? prompt))
            return TetherResponse.Failure("missing target or prompt");
        var target = Authorization.Resolve(TabOf(request.From), targetText);
        if (target is not TerminalNode terminal)
            return TetherResponse.Failure("target not found or ambiguous");
        if (!Authorization.CanAccess(TabOf(request.From), request.From, terminal.Id))
            return TetherResponse.Failure("no route");
        if (!_terminalViews.TryGetValue(terminal.Id, out var view) || !view.IsRunning)
            return TetherResponse.Failure("target not running");

        var chain = request.Chain.Count > 0
            ? request.Chain
            : _activeChains.GetValueOrDefault(request.From)?.ToList() ?? new List<Guid> { request.From };
        if (!CallChain.CanEnter(chain, terminal.Id, _settings.MaxCallDepth))
            return TetherResponse.Failure("call depth exceeded");

        var gate = _askGates.GetValueOrDefault(terminal.Id);
        if (gate is null) _askGates[terminal.Id] = gate = new SemaphoreSlim(1, 1);
        int queued = _askQueues.GetValueOrDefault(terminal.Id);
        if (queued >= 5) return TetherResponse.Failure("busy");
        _askQueues[terminal.Id] = queued + 1;
        await gate.WaitAsync();
        _askQueues[terminal.Id]--;

        var nextChain = chain.Append(terminal.Id).ToList();
        _activeChains[terminal.Id] = nextChain;
        using var detector = new IdleDetector(
            TimeSpan.FromMilliseconds(_settings.IdleMs),
            TimeSpan.FromMilliseconds(_settings.AskTimeoutMs));
        void Output(byte[] bytes) => detector.Push(bytes);
        void Exited(int _) => detector.Complete(TurnOutcome.TargetExited);
        view.OutputProduced += Output;
        view.ProcessExited += Exited;
        try
        {
            await view.SendPrompt(prompt);
            TurnResult result = await detector.Completion;
            return result.Outcome switch
            {
                TurnOutcome.Idle => TetherResponse.Success(result.Text),
                TurnOutcome.Timeout => TetherResponse.Failure("timeout", result.Text),
                _ => TetherResponse.Failure("target exited", result.Text)
            };
        }
        finally
        {
            view.OutputProduced -= Output;
            view.ProcessExited -= Exited;
            _activeChains.Remove(terminal.Id);
            gate.Release();
        }
    }

    private IReadOnlyDictionary<string, string> NodeEnvironment(Guid nodeId)
    {
        // The host may itself run under a no-color shell (Codex does); terminal children should
        // advertise the capabilities of the embedded xterm instead of inheriting that host policy.
        Environment.SetEnvironmentVariable("NO_COLOR", null);
        string path = AppContext.BaseDirectory + Path.PathSeparator +
                      (Environment.GetEnvironmentVariable("PATH") ?? "");
        return new Dictionary<string, string>
        {
            ["TETHER_PIPE"] = _pipeName,
            ["TETHER_NODE"] = nodeId.ToString(),
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["FORCE_COLOR"] = "3",
            ["PATH"] = path
        };
    }

    private void PrimeAgent(TerminalNode node)
    {
        if (!_settings.SeedAgentInstructions) return;
        try
        {
            AgentPrimer.Write(
                node.WorkingDirectory,
                node,
                Authorization.Neighbors(TabOf(node.Id), node.Id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowRecoveryNotice($"Nao foi possivel atualizar AGENTS.md: {e.Message}");
        }
    }
}
