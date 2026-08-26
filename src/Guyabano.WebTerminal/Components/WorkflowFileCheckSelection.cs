using Guyabano.Messaging;

namespace Guyabano.WebTerminal.Components;

public sealed record WorkflowFileCheckSelection(
    string FilePath,
    WorkflowFileCheck Check);
