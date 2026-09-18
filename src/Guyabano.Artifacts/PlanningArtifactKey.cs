namespace Guyabano.Artifacts;

/// <summary>
/// A stable logical identity for a planning artifact. The key is independent of
/// content and survives revisions; for example <c>contracts/TicketClassifier</c>
/// names the same logical contract across every revision the planner produces.
/// </summary>
public sealed record PlanningArtifactKey(string Kind, string Name)
{
    /// <summary>The canonical <c>kind/name</c> form of the identity.</summary>
    public string Value => $"{Kind}/{Name}";

    /// <inheritdoc />
    public override string ToString() => Value;
}
