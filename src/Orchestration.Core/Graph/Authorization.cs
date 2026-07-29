using Orchestration.Core.Models;

namespace Orchestration.Core.Graph;

public static class Authorization
{
    public static bool CanAccess(Workspace workspace, Guid from, Guid target)
    {
        if (from == target) return false;

        var targetNode = workspace.Nodes.FirstOrDefault(node => node.Id == target);
        var fromNode = workspace.Nodes.FirstOrDefault(node => node.Id == from);
        if (targetNode is null || fromNode is null) return false;

        return workspace.Connections.Any(connection =>
            connection.SourceId == from && connection.TargetId == target ||
            connection.Bidirectional && connection.SourceId == target && connection.TargetId == from ||
            targetNode is NoteNode &&
            (connection.SourceId == target && connection.TargetId == from ||
             connection.SourceId == from && connection.TargetId == target) ||
            fromNode is NoteNode &&
            (connection.SourceId == target && connection.TargetId == from ||
             connection.SourceId == from && connection.TargetId == target));
    }

    public static IReadOnlyList<NodeBase> Neighbors(Workspace workspace, Guid from) =>
        workspace.Nodes.Where(node => CanAccess(workspace, from, node.Id)).ToList();

    public static NodeBase? Resolve(Workspace workspace, string value)
    {
        if (Guid.TryParse(value, out var id))
            return workspace.Nodes.FirstOrDefault(node => node.Id == id);

        var matches = workspace.Nodes
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
