using Orchestration.Core.Models;
using Xunit;

namespace Orchestration.Core.Tests;

public class BrowserNodeTests
{
    /// <summary>
    /// The address box is where "localhost:3000" gets typed. A bare https:// prefix would break
    /// exactly that primary case — dev servers speak http — so local hosts get http instead.
    /// </summary>
    [Theory]
    [InlineData("localhost:3000", "http://localhost:3000")]
    [InlineData("127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("example.com/docs", "https://example.com/docs")]
    [InlineData("https://claude.ai", "https://claude.ai")]
    [InlineData("http://interno:5000", "http://interno:5000")]
    [InlineData("  example.com  ", "https://example.com")]
    [InlineData("   ", "")]
    // A "://" anywhere in the string used to read as "already schemed", so a URL-valued query
    // parameter defeated the check and this came back with no scheme at all.
    [InlineData("example.com?next=http://x", "https://example.com?next=http://x")]
    public void CompleteUrl_FillsTheSchemeTheUserDidNotType(string typed, string expected)
    {
        Assert.Equal(expected, BrowserNode.CompleteUrl(typed));
    }

    /// <summary>
    /// The wire name is a contract: agents read it from `tether list` and from the seeded
    /// AGENTS.md, so pinning it here catches an accidental rename that would break every consumer.
    /// </summary>
    [Fact]
    public void Label_ReturnsTheWireNameForEveryNodeKind()
    {
        Assert.Equal("terminal", NodeKinds.Label(new TerminalNode()));
        Assert.Equal("note", NodeKinds.Label(new NoteNode()));
        Assert.Equal("browser", NodeKinds.Label(new BrowserNode()));
    }
}
