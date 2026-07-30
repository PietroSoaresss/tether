using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Orchestration.App.Views;

/// <summary>
/// A Grid that can change the mouse cursor. <c>UIElement.ProtectedCursor</c> is protected, so a
/// subclass is the only way to reach it — and without a cursor change an armed tool is invisible.
/// </summary>
public sealed class CursorGrid : Grid
{
    private readonly Dictionary<InputSystemCursorShape, InputCursor> _cache = new();

    public void SetCursor(InputSystemCursorShape shape)
    {
        if (!_cache.TryGetValue(shape, out var cursor))
            _cache[shape] = cursor = InputSystemCursor.Create(shape);
        ProtectedCursor = cursor;
    }
}
