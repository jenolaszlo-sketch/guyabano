namespace Guyabano.CodeGeneration.Planning;

internal sealed class DefaultArchitecturePracticeProvider
    : IArchitecturePracticeProvider
{
    private static readonly IReadOnlyList<ArchitecturePractice> Practices =
    [
        Create(
            "api.problem-details",
            "Standards-based HTTP errors",
            "Use standards-based Problem Details responses for HTTP API errors unless an explicit public error contract requires another representation.",
            "HTTP APIs that need machine-readable validation, conflict, not-found, or server error details.",
            "Improves interoperability and avoids inventing an incompatible error envelope."),
        Create(
            "api.idempotent-state-transition",
            "Idempotent state-setting operations",
            "Treat repeated requests that set a resource to an already-achieved state as successful unless the domain explicitly requires a conflict.",
            "PUT, PATCH, completion, activation, cancellation, and other state-setting operations.",
            "Makes retries safe and follows normal HTTP state-transition semantics."),
        Create(
            "validation.boundary-and-domain",
            "Validate at boundaries and protect domain invariants",
            "Reject malformed input at the transport boundary and enforce important invariants again in the application or domain layer.",
            "Externally supplied commands, DTOs, configuration, and messages that affect domain state.",
            "Produces useful client errors while preventing non-HTTP callers from bypassing invariants."),
        Create(
            "concurrency.atomic-mutation",
            "Keep state transitions atomic",
            "Place read-modify-write state transitions behind one atomic persistence operation rather than composing separate reads and writes in application code.",
            "Concurrent mutation, counters, inventory, completion flags, and other race-sensitive state changes.",
            "Prevents lost updates and keeps concurrency responsibility in the storage boundary."),
        Create(
            "dotnet.options.validate-on-start",
            "Validate required configuration at startup",
            "Bind typed options and validate required configuration during application startup when delayed failure provides no benefit.",
            ".NET services with required provider, endpoint, credential-reference, or operational configuration.",
            "Turns deployment mistakes into immediate, diagnosable startup failures."),
        Create(
            "testing.observable-behavior",
            "Test observable contracts",
            "Derive tests from acceptance criteria and externally observable contracts instead of implementation details.",
            "Unit, integration, and acceptance tests for public behavior.",
            "Preserves refactorability and ensures tests verify the intended outcome.")
    ];

    public IReadOnlyList<ArchitecturePractice> GetPractices() => Practices;

    private static ArchitecturePractice Create(
        string id,
        string title,
        string guidance,
        string applicability,
        string reason) =>
        new()
        {
            Id = id,
            Title = title,
            Guidance = guidance,
            Applicability = applicability,
            Reasons = [reason],
            Scope = "Established"
        };
}
