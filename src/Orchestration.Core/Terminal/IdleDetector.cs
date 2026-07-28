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
    private readonly AnsiFilter _filter = new();
    private readonly TurnCollapser _collapser = new();
    private readonly TaskCompletionSource<TurnResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();

    private ITimer? _idleTimer;
    private ITimer? _timeoutTimer;
    private bool _finished;

    public IdleDetector(TimeSpan idle, TimeSpan timeout, TimeProvider? time = null)
    {
        _idle = idle;
        TimeProvider provider = time ?? TimeProvider.System;

        // Both timers start now: a target that never says anything still has to resolve.
        _idleTimer = provider.CreateTimer(_ => Complete(TurnOutcome.Idle), null, idle, System.Threading.Timeout.InfiniteTimeSpan);
        _timeoutTimer = provider.CreateTimer(_ => Complete(TurnOutcome.Timeout), null, timeout, System.Threading.Timeout.InfiniteTimeSpan);
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
            _idleTimer?.Change(_idle, System.Threading.Timeout.InfiniteTimeSpan);
        }
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
