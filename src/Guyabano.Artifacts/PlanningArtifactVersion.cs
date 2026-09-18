namespace Guyabano.Artifacts;

/// <summary>
/// Pins one revision of one logical planning artifact, for example
/// <c>contracts/TicketClassifier@3</c>.
/// </summary>
public sealed record PlanningArtifactVersion(PlanningArtifactKey Key, int Revision)
{
    /// <summary>The canonical <c>kind/name@revision</c> form of the version.</summary>
    public string Value => $"{Key.Value}@{Revision}";

    /// <inheritdoc />
    public override string ToString() => Value;
}
