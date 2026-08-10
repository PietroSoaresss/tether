namespace Orchestration.Core.Terminal;

/// <summary>
/// Delivers a delegated prompt to an agent's terminal: the text first, the Enter afterwards.
/// Sent as one buffer the two arrive in a single stdin read, and every modern agent TUI reads a
/// burst like that as a paste — where a carriage return is a literal newline in the input box and
/// never a submit. The prompt then sits in the target's chat unsent, which is the whole bug this
/// exists to prevent. The gap is what makes the Enter a keystroke of its own.
/// </summary>
public static class PromptSubmission
{
    /// <summary>
    /// The gap is a real wait on a real machine, so it is a caller's parameter rather than a
    /// constant here: how long a TUI takes to close its paste burst varies with the agent and
    /// with how loaded the box is, and a value that works on an idle laptop is not a law.
    /// </summary>
    public static async Task Send(
        Action<string> write,
        string prompt,
        TimeSpan gap,
        TimeProvider? time = null)
    {
        write(prompt);
        await Task.Delay(gap, time ?? TimeProvider.System);
        write("\r");
    }
}
