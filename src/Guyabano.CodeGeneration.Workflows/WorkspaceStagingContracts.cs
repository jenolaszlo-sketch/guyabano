namespace Guyabano.CodeGeneration.Workflows;

public sealed record StagingValidationResult(bool Valid, string? Reason = null);

public sealed record WorkspaceStagingMutation(
    Guid SessionId,
    string MutationId,
    string BaselineRevision,
    DateTimeOffset CreatedAt,
    string StagingHostPath);

public sealed record WorkspacePromotion(
    Guid SessionId,
    string MutationId,
    string FromRevision,
    string ToRevision,
    bool Validated,
    DateTimeOffset PromotedAt,
    string? BackupPath);

public sealed class ConcurrentWorkspaceMutationException : InvalidOperationException
{
    public ConcurrentWorkspaceMutationException(string message) : base(message)
    {
    }
}

public sealed class StagingValidationException : InvalidOperationException
{
    public StagingValidationException(string message) : base(message)
    {
    }
}
