namespace Orchestration.Core.Models;

/// <summary>
/// A cable authorises a call, it does not carry bytes: `tether ask` only reaches a node the
/// caller is wired to. Direction is meaningful between terminals; for a note, any cable grants
/// both `note show` and `note edit`.
/// </summary>
public sealed class Connection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }
    public Guid TargetId { get; set; }
    public bool Bidirectional { get; set; }
}
