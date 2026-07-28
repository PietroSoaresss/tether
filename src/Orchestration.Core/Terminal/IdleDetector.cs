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
