namespace Guyabano.Messaging;

public sealed record WorkflowProgressEntry(
    string EntryId,
    string WorkflowId,
    WorkflowProgress Progress);
