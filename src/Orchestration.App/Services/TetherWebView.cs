using Microsoft.Web.WebView2.Core;

namespace Orchestration.App.Services;

/// <summary>
/// One browser process family for every WebView2 in the app — terminals and browser nodes alike.
/// Two environments pointing at the same user data folder must be created with identical options,
/// which is exactly the kind of agreement that silently rots when each view carries its own copy.
/// </summary>
public static class TetherWebView
{
    private static CoreWebView2Environment? _environment;
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public static async Task<CoreWebView2Environment> SharedEnvironmentAsync()
    {
        await Lock.WaitAsync();
        try
        {
            return _environment ??= await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: null,
                userDataFolder: Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Orchestration", "WebView2"),
                options: new CoreWebView2EnvironmentOptions());
        }
        finally
        {
            Lock.Release();
        }
    }
}
