# Guyabano Roadmap

## Vision

Guyabano is an opinionated, deterministic software-development workflow. It owns
the methodology, workspace lifecycle, validation, approvals, and completion
criteria while delegating bounded implementation and correction tasks to a
provider-agnostic coding executor.

```text
Guyabano workflow
  ├─ Understand request
  ├─ Inspect repository
  ├─ Research when necessary
  ├─ Architecture
  ├─ Task decomposition
  ├─ Implementation → ICodingExecutor
  ├─ Authoritative build and tests
  ├─ Independent review
  ├─ Correction → ICodingExecutor
  └─ Final validation
```

The central boundary is:

> Guyabano decides what must happen. Zhinu durably enforces the process. A coding
> executor attempts one bounded workspace change.

## Component responsibilities

### Guyabano

- Owns the software-development methodology and legal phase transitions
- Creates, leases, snapshots, and cleans up workspaces
- Defines architecture, decomposition, review, and approval gates
- Selects a coding executor for implementation and correction tasks
- Independently inspects actual workspace changes
- Runs authoritative builds and tests
- Decides whether requirements and quality gates are satisfied
- Presents progress, diagnostics, evidence, and intervention requests to users

### `ICodingExecutor`

- Attempts one specified coding task against one provided workspace
- Returns an execution result and provider-reported diagnostics
- May plan, iterate, invoke models, use tools, or delegate internally
- Does not control the overall workflow or decide that the project is complete

### Baize

- Provides model/provider abstraction
- May be composed into `BaizeCodingExecutor`
- Does not define Guyabano's workflow or the shared coding-executor contract

### Zhinu

- Persists and resumes Guyabano workflow state
- Enforces workflow transitions, retries, gates, and durable operation phases
- Records revision-bound evidence and audit history as those contracts mature
- Does not own Guyabano's coding methodology or provider-specific harness behavior

## Explicit non-goals

Do not introduce these concepts into the common coding-executor abstraction:

- `IAgent`
- Agent orchestration
- Generic planners
- Autonomous delegation APIs
- Generic memory
- Generic MCP abstractions
- A shared agent framework
- Provider-specific session or tool concepts

Providers may use any of these internally. They must not leak into the minimum
common contract unless two substantially different implementations prove a
portable need.

Do not extract a new Penghou package during the initial refactor. Keep the
abstraction inside Guyabano until it has survived real use with at least two
different coding harnesses.

## Phase 0 — Capture current behavior

Before changing architecture:

- Document the existing coding execution lifecycle.
- Identify where Baize, context construction, tools, workspace mutation,
  commands, build/test behavior, and result reporting are currently coupled.
- Add characterization tests around successful execution, failure,
  cancellation, and partial workspace changes.
- Record the existing result and progress information consumed by callers.

Exit criteria:

- The existing behavior is protected by focused tests.
- Every responsibility moving behind `ICodingExecutor` has an identified owner.
- The refactor can be evaluated without relying only on manual comparison.

## Phase 1 — Introduce the narrow executor contract

Start with the smallest useful contract:

```csharp
public interface ICodingExecutor
{
    Task<CodingResult> ExecuteAsync(
        CodingTask task,
        CancellationToken cancellationToken = default);
}
```

Initial common types should remain small:

```text
CodingTask
  ExecutionId
  TaskId
  Workspace
  Instructions
  ExpectedWorkspaceRevision
  Constraints/options

CodingResult
  Status
  Summary
  ProviderExecutionId
  Reported changed files
  Reported commands
  Diagnostics/errors
  Provider metadata
```

Identity rules:

- `TaskId` identifies the logical implementation or correction task.
- `ExecutionId` identifies a durable invocation and supports correlation or
  provider resume behavior.
- `ExpectedWorkspaceRevision` prevents silently applying work to stale state.
- Retry attempts must not be mistaken for logical task identity.

Exit criteria:

- Guyabano workflow code depends only on `ICodingExecutor` for implementation work.
- The base contract contains no Baize or provider-specific types.
- Architecture, build, test, review, and completion remain outside the executor.

## Phase 2 — Refactor the current implementation into Baize

Move current coding behavior behind `BaizeCodingExecutor` or an equivalently
clear name.

Suggested composition:

```text
BaizeCodingExecutor
  ├─ BaizeContextBuilder
  ├─ BaizeToolSet
  ├─ CodingPromptBuilder
  ├─ ExecutionLoop
  └─ CodingResultMapper
```

Guyabano may continue to provide the existing file, command, build, test, and
context services. The executor composes those services rather than inheriting a
large base class or duplicating them.

Exit criteria:

- Existing Guyabano behavior works through `BaizeCodingExecutor`.
- Baize-specific options remain in Baize registration/configuration.
- No provider branching exists in the main Guyabano workflow.
- Characterization and integration tests remain green.

## Phase 3 — Make workspace ownership explicit

Guyabano, not the executor, owns workspace lifecycle and authoritative state.

Initial workspace contract should represent at least:

```text
WorkspaceId
RootPath or opaque workspace location
BaselineRevision
CurrentRevision
Allowed path scope
Mutation lease identity
```

Guyabano responsibilities:

- Create or select the workspace.
- Acquire an exclusive mutation lease.
- Capture the baseline revision.
- Supply allowed paths and other resource constraints.
- Observe the resulting diff independently.
- Release or quarantine the workspace after execution.

Important durability rule:

> A Zhinu database lease cannot by itself fence filesystem writes from a stale
> external process.

For Guyabano-controlled tools, every mutation should verify the active workspace
execution identity. For unrestricted external harnesses, an ambiguous crash
must initially transition to intervention or workspace reconciliation rather
than blindly starting a second mutating executor.

Exit criteria:

- Only one mutating execution can own a workspace at a time.
- Stale Guyabano-controlled mutation tools reject writes.
- Ambiguous external-process termination has an explicit recovery policy.
- Workspace cleanup cannot race an active executor.

## Phase 4 — Separate claims from authoritative evidence

Executor results are useful reports, not automatically trusted evidence.

After each execution, Guyabano independently captures:

- Workspace revision before and after
- Actual changed files and diff
- Modifications outside allowed scopes
- Build command, exit code, and logs
- Test command, exit code, and logs
- Review findings and their resolution
- Approval identity where applicable

Provider-reported changed files, commands, or tests remain diagnostic unless
Guyabano explicitly promotes them to trusted evidence.

Evidence must be bound to the workspace revision it evaluated:

```text
Implementation revision N
        ↓
Build evidence for N
        ↓
Test evidence for N
        ↓
Review evidence for N
        ↓
Completion of N
```

Any relevant source change invalidates older build, test, and review evidence.

Exit criteria:

- Completion cannot depend solely on an executor's success claim.
- Build and test gates use Guyabano-observed results.
- Stale evidence is rejected after workspace mutation.
- Evidence is visible in workflow progress and diagnostics.

## Phase 5 — Add a substantially different executor

Implement one external coding harness whose internal architecture differs from
the Baize implementation, for example:

- `CodexCodingExecutor`
- `ClaudeCodeCodingExecutor`
- `CopilotCodingExecutor`
- `OpenCodeCodingExecutor`

The second executor should exercise different ownership assumptions. Ideally:

- Baize implementation: Guyabano owns context, tools, and iteration.
- External implementation: the provider owns much of its internal coding loop.

Integration concerns:

- Executable discovery and version reporting
- Authentication and configuration
- Structured task delivery
- Cancellation and process-tree termination
- Output parsing and diagnostic preservation
- Provider session/resume correlation
- Workspace mutation and crash ambiguity

Exit criteria:

- Both executors satisfy the same narrow interface.
- Main workflow code contains no provider-specific branching.
- Differences are handled through DI configuration, implementation-specific
  options, or proven capability descriptors.
- The base interface has not expanded merely to mirror one provider.

## Phase 6 — Executor selection and factual capabilities

Only after two implementations exist, add selection metadata if Guyabano needs it.

Possible descriptor:

```csharp
public interface ICodingExecutorDescriptor
{
    string Name { get; }
    CodingExecutorCapabilities Capabilities { get; }
}
```

Capabilities should be coarse and factual, such as:

- Supports resume
- Supports structured diagnostics
- Supports path restrictions
- Supports command restrictions
- Supports progress reporting
- Supports provider sessions

Do not add planner, memory, delegation, or tool APIs to the common contract.

Exit criteria:

- Selection can occur through configuration or explicit user choice.
- Unsupported requirements fail before workspace mutation.
- Provider-specific settings remain outside `CodingTask` unless they prove
  portable across implementations.

## Phase 7 — Formalize the Guyabano workflow on Zhinu

First express the methodology as an ordinary code-first Zhinu workflow:

```text
Analyze
→ Inspect
→ Research?
→ Architecture
→ Approval?
→ Decompose
→ ImplementTask
→ InspectDiff
→ Build
→ Test
→ Review
→ Fix loop
→ FinalValidation
→ Complete
```

Required invariants:

- Implementation cannot begin before required architecture or approval.
- Every completion path includes an authoritative build and test.
- Failed mandatory tests prevent completion.
- Review occurs against the same revision that was built and tested.
- A correction invalidates affected evidence and returns through validation.
- Commit and push are separately privileged from file modification.

Later, represent the same methodology as a hand-authored Zhinu
`WorkflowArtifact`. Natural-language methodology compilation should come only
after the artifact model and validator are proven.

Exit criteria:

- Guyabano resumes correctly after process loss at every major phase.
- Coding executor changes do not change workflow control flow.
- Workflow state, evidence, and user-visible task state have an explicit mapping.
- The methodology can be inspected independently of provider implementation.

## Phase 8 — Evaluate extraction

Consider extracting the coding-executor abstraction into a reusable package
such as `Penghou.Luban` only when all of the following are true:

- At least two substantially different executors are production-usable.
- Their common contract has remained stable through real tasks.
- Another application besides Guyabano has a concrete need for the abstraction.
- Workspace, cancellation, result, and evidence semantics are understood.
- Extraction reduces duplication rather than creating speculative indirection.

Until then, keep the contract inside Guyabano and allow it to evolve cheaply.

## Suggested internal structure

```text
Guyabano/
  Workflows/
    CodingWorkflow
    ArchitectureWorkflow
    ReviewWorkflow

  Coding/
    ICodingExecutor
    CodingTask
    CodingResult
    CodingExecutionStatus
    WorkspaceReference

  Coding/Baize/
    BaizeCodingExecutor
    BaizeContextBuilder
    BaizeToolSet
    BaizeResultMapper

  Coding/ExternalProvider/
    ExternalCodingExecutor
    ExternalProcessRunner
    ExternalResultMapper

  Workspace/
    IWorkspaceManager
    WorkspaceLease
    WorkspaceSnapshot
    WorkspaceEvidenceCollector

  Validation/
    DiffValidator
    BuildValidator
    TestValidator
    RequirementReviewer
```

This is a target responsibility map, not a requirement to create every type or
directory before it is needed.

## Near-term implementation order

1. Characterize the current execution path with tests.
2. Introduce `ICodingExecutor`, `CodingTask`, and `CodingResult` inside Guyabano.
3. Move current behavior into `BaizeCodingExecutor` through composition.
4. Keep build, test, review, and completion decisions in the Guyabano workflow.
5. Add workspace identity, baseline revision, and exclusive mutation ownership.
6. Capture actual diff and authoritative validation evidence independently.
7. Implement one substantially different external executor.
8. Reassess the common contract before adding capability interfaces.
9. Map the resulting workflow and evidence model onto Zhinu.
10. Consider extraction only after the boundary has proven stable.

## Success measures

- Provider implementations can be replaced without changing Guyabano workflow code.
- No provider-specific types appear in the common executor contract.
- Build, test, review, and approval gates cannot be skipped by an executor.
- Actual workspace changes are independently observed and validated.
- Stale or ambiguous executions cannot silently corrupt a workspace.
- Process loss resumes without duplicating an acknowledged coding execution.
- Adding the second executor requires composition, not conditionals throughout
  Guyabano.
- The common abstraction remains small after real use by two providers.
- Guyabano's methodology can later be represented as a validated Zhinu artifact.

## Relationship to the Zhinu roadmap

| Guyabano | Zhinu |
| --- | --- |
| `ICodingExecutor` | Activity implementation |
| `CodingTask` | Typed activity input |
| `CodingResult` | Typed activity output |
| Guyabano methodology | Code-first workflow, later workflow artifact |
| Workspace restrictions | Capability and resource scopes |
| Build/test/review results | Revision-bound evidence |
| Executor selection | Activity binding |
| Baize model calls | Bounded AI activities |
| External coding harness | Open-ended activity escape hatch |
| Review/fix loop | Deterministic quality gate and bounded loop |

Guyabano is the first vertical proving the compiled-workflow direction. Zhinu should
generalize only the runtime, capability, policy, and evidence concepts that Guyabano
demonstrates through real execution.
