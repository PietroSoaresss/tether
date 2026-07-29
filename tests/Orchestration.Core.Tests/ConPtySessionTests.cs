using System.Text;
using Orchestration.Core.Terminal;
using Xunit;

namespace Orchestration.Core.Tests;

public class ConPtySessionTests
{
    private const string Marker = "PROVA_CONPTY_OK";

    /// <summary>
    /// The whole point of ConPTY here: a real interactive shell, not a captured stdout.
    /// Regression guard for the STARTF_USESTDHANDLES fix - without it the child silently
    /// inherits the test runner's redirected handles and this test sees nothing.
    /// </summary>
    [Fact]
    public async Task InteractiveShell_EchoesWhatWeType()
    {
        var output = new StringBuilder();
        var sawMarker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoder = Encoding.UTF8.GetDecoder();

        using var session = new ConPtySession();
        session.OutputReceived += data =>
        {
            var chars = new char[decoder.GetCharCount(data, 0, data.Length, false)];
            int count = decoder.GetChars(data, 0, data.Length, chars, 0, false);
            lock (output)
            {
                output.Append(chars, 0, count);
                // Twice: once echoed as it is typed, once as the command's own result.
                if (Occurrences(output.ToString(), Marker) >= 2) sawMarker.TrySetResult(true);
            }
        };

        session.Start("powershell.exe -NoLogo -NoProfile", Path.GetTempPath(), 100, 30);
        Assert.Equal(SessionState.Running, session.State);

        await Task.Delay(1500);
        session.Write($"echo {Marker}\r\n");

        var completed = await Task.WhenAny(sawMarker.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(completed == sawMarker.Task, $"O shell nao ecoou o marcador. Saida bruta:\n{output}");

        // A real terminal, so real VT sequences must be present.
        Assert.Contains('\x1b', output.ToString());
    }

    [Fact]
    public void Kill_TakesTheProcessTreeWithIt()
    {
        using var session = new ConPtySession();
        session.Start("powershell.exe -NoLogo -NoProfile", Path.GetTempPath(), 80, 24);
        int pid = session.ProcessId;

        session.Kill();
        Thread.Sleep(1500);

        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
    }

    [Fact]
    public void Resize_BeforeStart_IsIgnoredRatherThanThrowing()
    {
        using var session = new ConPtySession();
        session.Resize(120, 40);
        Assert.Equal(SessionState.NotStarted, session.State);
    }

    [Fact]
    public async Task Start_InjectsExtraEnvironmentWithoutLosingTheInheritedPath()
    {
        var output = new StringBuilder();
        var sawValue = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new ConPtySession();
        session.OutputReceived += data =>
        {
            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(data));
                if (output.ToString().Contains("ENV_OK", StringComparison.Ordinal))
                    sawValue.TrySetResult(true);
            }
        };

        session.Start(
            "powershell.exe -NoLogo -NoProfile -Command \"Write-Output $env:TETHER_TEST\"",
            Path.GetTempPath(),
            extraEnvironment: new Dictionary<string, string> { ["TETHER_TEST"] = "ENV_OK" });

        var completed = await Task.WhenAny(sawValue.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(completed == sawValue.Task, $"A variavel nao chegou ao filho. Saida:\n{output}");
    }

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
