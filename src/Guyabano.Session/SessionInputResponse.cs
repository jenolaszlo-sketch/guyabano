namespace Guyabano.Session;

/// <summary>
/// Durable acknowledgement that one pending session input request was answered
/// and its Zhinu signal command committed.
/// </summary>
public sealed record SessionInputResponseReceipt(
    GuyabanoSessionId SessionId,
    Guid WorkflowRunId,
    Guid RequestEventId,
    Guid ResponseId,
    string SignalName,
    Guid SignalId,
    long ZhinuEventSequence,
    Guid SessionEventId,
    bool WasBuffered);

public sealed class SessionInputAlreadyProvidedException(string message) :
    InvalidOperationException(message);
