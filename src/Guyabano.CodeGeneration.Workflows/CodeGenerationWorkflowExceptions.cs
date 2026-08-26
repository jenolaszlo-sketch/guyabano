namespace Guyabano.CodeGeneration.Workflows;

/// <summary>Base exception for all Guyabano workflow failures.</summary>
public class CodeGenerationWorkflowException : Exception
{
    public CodeGenerationWorkflowException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when a workflow phase produces an invalid or missing result.</summary>
public sealed class CodeGenerationPhaseException : CodeGenerationWorkflowException
{
    public CodeGenerationPhaseException(string phaseName, string message, Exception? innerException = null)
        : base($"Phase '{phaseName}' failed: {message}", innerException)
    {
        PhaseName = phaseName;
    }

    public string PhaseName { get; }
}

/// <summary>Thrown when the planning phase produces an invalid plan.</summary>
public sealed class CodeGenerationPlanningException : CodeGenerationWorkflowException
{
    public CodeGenerationPlanningException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when architecture review rejects a plan after all retry passes.</summary>
public sealed class CodeGenerationArchitectureRejectedException : CodeGenerationWorkflowException
{
    public CodeGenerationArchitectureRejectedException(int passes, string lastFeedback)
        : base($"Architecture was not approved after {passes} pass(es). Last feedback: {lastFeedback}")
    {
        Passes = passes;
        LastFeedback = lastFeedback;
    }

    public int Passes { get; }
    public string LastFeedback { get; }
}

/// <summary>Thrown when a build/test correction cycle exhausts all attempts.</summary>
public sealed class CodeGenerationBuildExhaustedException : CodeGenerationWorkflowException
{
    public CodeGenerationBuildExhaustedException(string taskName, int attempts, string lastDiagnostics)
        : base($"Task '{taskName}' failed after {attempts} build/test correction attempt(s). " +
               $"Last diagnostics: {lastDiagnostics}")
    {
        TaskName = taskName;
        Attempts = attempts;
        LastDiagnostics = lastDiagnostics;
    }

    public string TaskName { get; }
    public int Attempts { get; }
    public string LastDiagnostics { get; }
}
