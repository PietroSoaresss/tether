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
    public void CompleteUrl_FillsTheSchemeTheUserDidNotType(string typed, string expected)
    {
        Assert.Equal(expected, BrowserNode.CompleteUrl(typed));
    }
}
