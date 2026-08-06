using Orchestration.Core.Models;

namespace Orchestration.Core.Graph;

/// <summary>
/// Cables authorize, and cables live on one canvas — so authorization is scoped to a canvas too.
/// Two nodes on different tabs of the same project cannot reach each other, which is the same rule
/// as before tabs existed, just now stated where it can be seen.
/// </summary>
public static class Authorization
{
    public static bool CanAccess(CanvasTab canvas, Guid from, Guid target)
    {
        if (from == target) return false;

        var targetNode = canvas.Nodes.FirstOrDefault(node => node.Id == target);
        var fromNode = canvas.Nodes.FirstOrDefault(node => node.Id == from);
        if (targetNode is null || fromNode is null) return false;

        return canvas.Connections.Any(connection =>
            connection.SourceId == from && connection.TargetId == target ||
            connection.Bidirectional && connection.SourceId == target && connection.TargetId == from ||
            targetNode is NoteNode &&
            (connection.SourceId == target && connection.TargetId == from ||
             connection.SourceId == from && connection.TargetId == target) ||
            fromNode is NoteNode &&
            (connection.SourceId == target && connection.TargetId == from ||
             connection.SourceId == from && connection.TargetId == target));
    }

    public static IReadOnlyList<NodeBase> Neighbors(CanvasTab canvas, Guid from) =>
        canvas.Nodes.Where(node => CanAccess(canvas, from, node.Id)).ToList();

    public static NodeBase? Resolve(CanvasTab canvas, string value)
    {
        if (Guid.TryParse(value, out var id))
            return canvas.Nodes.FirstOrDefault(node => node.Id == id);

        var matches = canvas.Nodes
            .Where(node => string.Equals(node.Title, value, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}

public static class CallChain
{
    public static bool CanEnter(IReadOnlyCollection<Guid> chain, Guid target, int maxDepth) =>
        chain.Count < maxDepth && !chain.Contains(target);
}
