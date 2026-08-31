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

Detailed session progress, remaining work, and verification evidence are tracked
only in [`docs/session-backlog.md`](docs/session-backlog.md).

The target execution plane has two intentionally different branches:

```text
                         Guyabano
                            │
                workflow and methodology
                            │
                          Zhinu
                 durable step orchestration
                            │
                     ICodingExecutor
                            │
              ┌─────────────┴─────────────┐
              │                           │
     model-backed executor        agent-backed executor
              │                           │
            Baize                    A2A client
              │                           │
   ┌──────────┼──────────┐      ┌─────────┼──────────┐
 OpenAI     Gemini    DeepSeek  Codex    Claude    OpenCode
                                  via native A2A support or gateways
```

`ICodingExecutor` is the stable capability boundary above both branches. Baize
normalizes model providers; A2A normalizes communication with opaque coding
agents. Neither Baize nor A2A owns Guyabano's methodology, durable retries,
workspace authority, or final acceptance decision.

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

### A2A

- Provides discovery and communication with opaque remote or local agents.
- Maps A2A messages, tasks, status updates, and artifacts to one bounded coding
  executor invocation.
- Does not replace Zhinu: an A2A task is provider execution state, not the
  authoritative Guyabano workflow.
- Does not grant filesystem authority, enforce workspace containment, prove
  cancellation, or make agent-reported artifacts authoritative evidence.
- Codex, Claude Code, OpenCode, and future agents may connect through native A2A
  support or narrowly scoped gateways; native support must be verified rather
  than assumed.

### Zhinu

- Persists and resumes Guyabano workflow state
- Enforces workflow transitions, retries, gates, and durable operation phases
- Records revision-bound evidence and audit history as those contracts mature
- Does not own Guyabano's coding methodology or provider-specific harness behavior

### Hetu and Cangjie

- Hetu owns the durable, incrementally published structural code graph.
- Cangjie owns attributable textual observations and immutable context snapshots.
- Guyabano owns repository identity, selection strategy, disclosure policy, and
  prompt composition.
- Zhinu persists the index, selection, and snapshot references as ordinary typed
  workflow-step results.
- Structural graph facts are not duplicated wholesale into Cangjie.

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

This does not prohibit a small, typed application-level capability gateway.
Such a gateway describes an outcome Guyabano needs, such as read-only web
research, and remains separate from `ICodingExecutor`. It must not expose MCP
tool schemas, arbitrary nested agent tools, or provider-specific session types.

Do not extract a new Penghou package before the initial Baize vertical,
workspace-safety boundary, and authoritative-evidence semantics are proven
inside Guyabano. A deliberately unstable preview package may then enable the
second executor; a stable public contract still requires two substantially
different harnesses to have exercised it.

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
        CodingWorkspace workspace,
        CancellationToken cancellationToken = default);
}
```

Initial common types should remain small:

```text
CodingTask
  ExecutionId
  TaskId
  Objective and optional description
  Constraints and acceptance criteria
  Relevant and allowed files
  ExpectedWorkspaceRevision

CodingWorkspace
  WorkspaceId
  Local root or provider-neutral location
  Baseline revision
  Allowed path scope
  Mutation lease/execution identity

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
- Workspace identity and mutation authority are supplied separately from the
  semantic task so the same task can be attempted in an isolated replacement
  workspace.
- Reported changes, commands, and verification are executor claims until the
  host independently observes them.

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

## Phase 5 — Extract a preview library and add a substantially different executor

Once Phases 0–4 have fixed the responsibility boundary, extract the proven
contracts under the working name `Penghou.Luban`. The name is provisional until
the repository and NuGet IDs are created, but the package boundary is not:

```text
Penghou.Luban
  core task, workspace, result, change, verification-report,
  diagnostics, path-safety, and conformance contracts

Penghou.Luban.Baize
  Baize-backed execution, structured edit protocol, prompting,
  and bounded model-driven repair

Penghou.Luban.A2A
  A2A client adapter, task/artifact normalization, reconciliation,
  and protocol-version negotiation

Agent gateways (separate processes or optional packages)
  expose Codex, Claude Code, OpenCode, or another coding harness through A2A
```

The core package must not depend on Guyabano, Zhinu, Baize, A2A, Hetu, Nuwa,
Codex, or Microsoft dependency injection. Adapter packages may depend on the
products and protocols they integrate. DI registration and keyed resolution
belong in adapter or hosting packages rather than the core abstraction.

### Phase 5A — Core preview and Baize migration

- Move only the already-proven `ICodingExecutor`, `CodingTask`,
  `CodingWorkspace`, `CodingResult`, change, diagnostic, and reported
  verification contracts.
- Provide canonical path containment, traversal rejection, allowed-path
  enforcement, cancellation, bounded result collections, and privacy-safe
  diagnostics.
- Make command execution opt-in with explicit executable/argument policy,
  timeout, working-directory containment, cancellation, and complete result
  reporting. Never treat a command reported by an executor as authorization.
- Report created, modified, deleted, and renamed paths with hashes where
  available. Full diffs are optional, bounded, and potentially sensitive; the
  host's independently observed diff remains authoritative.
- Ship a capability-gated executor conformance harness covering creation,
  modification, multi-file changes, boundary rejection, cancellation,
  verification failure, and failure without unrelated corruption.
- Move `BaizeCodingExecutor` behind the package contract without moving
  Guyabano's methodology, workspace lifecycle, or authoritative gates.
- Keep filesystem context as the baseline. Hetu and Nuwa integrations remain
  optional adapter composition, never required core dependencies.

Do not put provider continuation/session models or a workspace transaction
manager in the first core contract. Opaque continuation becomes portable only
if two executors demonstrate compatible lifecycle and security semantics.
Workspace creation, snapshot, commit, rollback, quarantine, and mutation leases
remain host-owned; a reusable transaction abstraction can move later if another
host proves the same boundary.

Executor-requested verification may drive an internal bounded repair loop, but
its results remain reported evidence. Guyabano independently captures the final
diff and reruns mandatory build, test, format, analysis, review, and approval
gates against the exact resulting workspace revision.

Structured edits should prefer explicit create, replace, patch, delete, and
move operations. Every path is canonicalized beneath the workspace root;
ambiguous edits and structurally unrecoverable output fail rather than being
guessed. Nuwa may repair uniquely recoverable response structure, not infer
code changes.

### Phase 5B — A substantially different agent executor over A2A

Implement one external coding harness whose internal architecture differs from
the Baize implementation. Prefer a generic `A2ACodingExecutor` plus a narrow
gateway for one real coding agent, for example:

- Codex behind an A2A gateway
- Claude Code behind an A2A gateway
- OpenCode behind an A2A gateway
- another native A2A coding agent

If the chosen agent has no usable A2A surface, a direct process adapter may be
used to learn its lifecycle, but that provider-specific process contract stays
outside `Penghou.Luban` core and should be replaceable by an A2A gateway.

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

The A2A mapping should be explicit:

```text
CodingTask       → A2A input message/data part
ExecutionId      → idempotent client correlation metadata
A2A task/context → provider execution reference persisted by Zhinu
A2A artifacts    → reported CodingResult content
status updates   → optional progress diagnostics
GetTask          → restart/reconnect reconciliation
CancelTask       → best-effort remote cancellation request
```

Critical results must come from terminal task state and artifacts, not transient
status messages. After disconnect or restart, Guyabano reconciles the recorded
A2A task before creating another mutating invocation. Cancellation does not
prove that the remote agent stopped writing; workspace fencing and reconciliation
still apply.

Agent Cards and advertised skills inform selection but are untrusted capability
claims. Hosts allowlist endpoints, authentication policy, supported protocol
versions, skills, input/output modes, and any required A2A extensions before
workspace access is granted.

Codex-, Claude-, and OpenCode-specific session IDs, sandbox settings, command
events, and tool protocols remain inside their gateway. The minimum core API
remains non-streaming; A2A streaming is mapped to optional progress only after a
real host requires it.

Exit criteria:

- The Baize and A2A executors satisfy the same narrow interface.
- Main workflow code contains no provider-specific branching.
- Differences are handled through DI configuration, implementation-specific
  options, or proven capability descriptors.
- The base interface has not expanded merely to mirror one provider.
- Guyabano can execute the same bounded task through Baize or an A2A coding
  agent without changing its engineering workflow.
- A package consumer can use Luban without referencing Guyabano.

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

For an A2A executor, its descriptor is the host-validated projection of an
Agent Card plus local policy; no A2A transport type leaks into the Luban core
contract, and an advertised skill is not accepted as proof that workspace or
command restrictions are enforced.

Do not add planner, memory, delegation, or tool APIs to the common contract.

Exit criteria:

- Selection can occur through configuration or explicit user choice.
- Unsupported requirements fail before workspace mutation.
- Provider-specific settings remain outside `CodingTask` unless they prove
  portable across implementations.

## Phase 6A — Agent capability gateways

After session provenance and the first real dogfood run are reliable, introduce
a narrow boundary through which workflows request typed logical capabilities
from external agents. Capability routing is distinct from model routing,
coding-executor selection, and Zhinu workflow routing:

```text
Guyabano workflow
  -> typed capability request
  -> capability router
  -> Codex, OpenCode, or a later native provider
  -> normalized, validated result
  -> immutable session evidence
```

Start with read-only `WebResearch`. Useful Guyabano cases include current
official documentation, package/version research, vulnerability research, and
later model-pricing snapshots. Do not begin with repository mutation, shell
execution, arbitrary tool invocation, or a generic capability schema; those
overlap existing workspace and executor trust boundaries and require separate
evidence.

Initial contracts should remain internal to Guyabano and small:

- stable `CapabilityId` and explicit availability states such as `Available`,
  `QuotaLimited`, `AuthenticationRequired`, `TemporarilyUnavailable`, and
  `Unsupported`;
- a provider registry and deterministic priority router;
- a typed `IWebResearchProvider` request/result contract;
- source URLs, retrieval time, provider/agent/model identity when available,
  duration, outcome, and fallback-chain provenance;
- cancellation and typed failure classification distinguishing quota,
  authentication, permission, transient, malformed-result, and policy failures;
- preferred and required source-domain policy, with primary sources required
  for sensitive structured facts such as model pricing.

Codex should be the first provider when its programmatic integration is
available. OpenCode may later provide the same capability using its own tool or
MCP ecosystem. Guyabano must not know which underlying search tool or protocol
was used. Native MCP may eventually become another provider without changing
workflow contracts; implementing an MCP client is not part of this phase.

Capability results are untrusted external data, never executable instructions.
Guyabano validates their schema and source policy, publishes the normalized
result as a typed immutable artifact, records it in the session ledger, and
pins its identity into the consuming Zhinu step. Replay reuses the recorded
result; provider availability or pricing changes must not silently alter an
already-started workflow run.

Fallback is allowed only for explicitly recoverable failures. Quota exhaustion
is a routing signal, not something Guyabano attempts to bypass. Permission or
policy rejection must remain visible and must not trigger repeated hidden
retries.

Required tests:

- registration, capability discovery, availability filtering, explicit
  preference, deterministic priority, and no-provider behavior;
- quota/transient fallback versus authentication/permission/policy rejection;
- cancellation, invalid-result rejection, source-domain enforcement, and full
  provenance preservation;
- a reusable provider conformance suite;
- one optional Codex integration test that validates shape, provenance, and
  official-source policy without asserting volatile facts or prices.

Exit criteria:

- A Guyabano workflow can request official-source web research without knowing
  whether Codex, OpenCode, or a native implementation supplied it.
- The normalized result and routing decision are auditable and reproducible
  from session/workflow evidence.
- Adding a second provider requires composition and conformance tests, not
  workflow conditionals.
- No MCP, arbitrary tool, or provider-native type leaks into workflow or
  capability contracts.

## Phase 7 — Formalize the Guyabano workflow on Zhinu

First express the methodology as an ordinary code-first Zhinu workflow:

Current foundation:

- The code-first workflow uses typed, keyed class-based Zhinu steps for
  planning, architecture, decomposition, scaffolding, generation, build, and
  checkpoints.
- Shared `WorkflowStepReference<TInput,TOutput>` values bind workflow calls and
  DI registrations to one compile-time contract.
- Orchestration, bounded review/repair loops, scheduling waves, and completion
  decisions remain visible in `CodeGenerationWorkflow.RunAsync`.
- Step implementations and their dependencies are resolved in a fresh scope
  for every attempt; completed replay does not resolve them.
- Retry heartbeat context is owned outside ephemeral step instances and is
  isolated by workflow run, durable step key, and revision.
- No compensation is declared until an operation has a genuine reversible
  contract.
- Workflow version 3 indexes the configured repository through Hetu, selects a
  bounded public surface or symbol neighborhood, and pins the exact rendered
  selection in a Cangjie snapshot before planning.
- Repository selection is bound to Hetu's exact publication receipt and fails
  if a newer graph publication appears between index and selection.
- Cangjie persists selected observations atomically and treats equivalent
  deterministic snapshot writes as safe retries.
- Repository context remains local unless the host explicitly enables bounded
  prompt disclosure.

Repository-intelligence follow-ups:

- Reindex after accepted workspace mutations and bind build, test, and review
  evidence to the resulting workspace revision.
- Record reviewed architecture decisions and failure lessons as keyed Cangjie
  revisions, then relate them to the evidence and artifacts they derive from.
- Add task-aware Hetu seed selection from planned contracts and affected files.
- Add a host-visible disclosure preview showing which Cangjie snapshot and how
  many characters will be sent before a model route receives repository context.

### Guyabano sessions

Introduce a long-lived `GuyabanoSession` as the user-visible and auditable
boundary around one evolving engineering effort. A session survives individual
Zhinu workflow runs and correlates the workspace, workflow history, memories,
code-graph publications, model executions, artifacts, clarifications, and
approvals under one stable identity.

```text
Guyabano session
  ├── append-only interaction and audit timeline
  ├── one evolving workspace with staged mutations
  ├── one or more Zhinu workflow runs and step revisions
  ├── Cangjie decisions, evidence, lessons, and context snapshots
  ├── Hetu code-graph publications for exact workspace revisions
  ├── Baize model-execution evidence
  └── typed artifacts, builds, tests, reviews, and approvals
```

Identity rules:

- `SessionId` identifies the long-lived engineering effort and its output
  workspace; it is not a Zhinu run ID.
- Treat project/workspace, session, workflow definition, and workflow run as
  distinct identities even when the first product experience creates one
  session per project. This preserves a future path to multiple sessions,
  branches, or efforts within one project without changing durable IDs.
- A session may contain multiple workflow runs, restarts, continuations, and
  interactive operations.
- Every persisted reference must be traceable to the relevant session, workflow
  run, durable step key and revision, workspace revision, and producer where
  those identities apply.
- Cangjie scopes and snapshots, Hetu repository and index identities, Zhinu
  artifacts, and filesystem manifests remain owned by their respective systems;
  the session correlates them rather than copying all payloads into one store.

Default embedded storage should follow the same ownership boundary:

```text
operational session catalog
  └─ session routing and global project/session lookup

session/{session-id}/
  ├─ session.db       # Siming immutable session evidence
  ├─ workflow.db      # Zhinu state for every workflow run in this session
  ├─ workspace/       # accepted project revision
  ├─ staging/         # isolated candidate revisions
  └─ artifacts/       # scoped artifact envelopes or references
```

Do not create one Zhinu database per workflow run. All runs, restarts, child
workflows, and recovery workflows belonging to a session share that session's
workflow database. Different sessions can then execute independently without a
global workflow database becoming a contention, retention, export, or deletion
boundary. Shared workflow definitions remain application registrations rather
than being copied into each database.

The operational catalog must map every workflow run to exactly one session and
route it to the correct workflow store. Long-lived hosts must bound and evict
open session runtime/store handles. Cross-session queries use catalog summaries;
they must not require opening every session database.

The initial interactive shell should put project/session create, search, and
resume in a left navigation rail. The larger right workspace should reserve a
resizable upper region for live workflow activity/graph visualization and use
the main lower region for conversation history, progress/evidence, and the
prompt or clarification composer. Later, selecting a workflow node scopes
inspection and conversation to that activity and exposes safe preview, adjust,
and rerun actions. These screens consume catalog/projection APIs rather than raw
SQLite databases.

Add an append-only session event log with ordered, immutable envelopes containing
actor, timestamp, event type, causation, correlation, and relevant cross-system
references. Important events include user and assistant messages, workflow and
step lifecycle changes, user-input requests and responses, clarifications,
approvals, model invocations, context selections, artifact publications,
workspace staging and promotion, builds, tests, code-graph publications,
invalidation previews, and reruns. Large or sensitive payloads remain in their
authoritative stores and are referenced by bounded metadata and content hashes.

Interactive workflow behavior:

- A workflow may durably wait for user input through a Zhinu signal.
- The request and response are also recorded as session events; accepted
  clarification is promoted deliberately into keyed Cangjie knowledge rather
  than treating all conversation as memory.
- Guyabano relates clarification to affected requirements, decisions, contracts,
  tasks, and artifacts, uses Hetu for code impact where applicable, and asks
  Zhinu for a preview of the affected restart subtree.
- Material cascades are presented for approval before mutation. The proposed
  impact, user decision, applied invalidation, reused steps, rerun steps, and
  resulting revisions are all auditable.
- Selective regeneration writes to a staging workspace, validates the staged
  revision, then promotes it into the session workspace. Failed mutations cannot
  silently become the session's current revision.

Create a focused `Guyabano.Session` component for session identity, commands,
events, projections, audit queries, clarification and approval models, and
cross-product reference types. A persistence adapter may provide an embedded
SQLite event store. Penghou-facing adapters translate Zhinu, Cangjie, Hetu, and
Baize activity into session references and events, while workflow sequencing,
dependency declarations, acceptance gates, and restart policy remain visible in
Guyabano's workflow code.

Required tests:

- Unit tests cover command validation, idempotent event append, causation and
  correlation, clarification classification, revision correspondence, and
  cross-store reconciliation rules.
- Embedded integration tests use real Zhinu SQLite, Cangjie SQLite, Hetu
  Ladybug, and filesystem artifacts to prove restart recovery, artifact
  publication, context snapshots, incremental code indexing, and audit
  reconstruction.
- One vertical dogfood test changes a requirement after successful generation,
  previews the cascade, reruns only affected nodes, reuses unaffected siblings,
  promotes a verified workspace revision, and reconstructs who did what and why.

Session exit criteria:

- An operator can reconstruct the ordered history of user, Guyabano, model,
  workflow, tool, and CI actions without relying on transient UI state or chat
  history.
- Every model-produced change identifies the exact Cangjie context snapshot and
  Hetu code publication available to it.
- A clarification can produce a previewable, dependency-aware cascade and a
  selective Zhinu rerun without creating a disconnected output workspace.
- Decisions and evidence superseded by a rerun remain historically accessible
  while current projections resolve to the promoted session revision.
- A consistency audit detects missing or mismatched workflow artifacts, memory
  references, graph publications, workspace revisions, and validation evidence.

### Deferred extraction candidate: `Penghou.Hongxian`

`Penghou.Hongxian` (红线, "red thread") is the working name for a reusable
durable-session kernel. Its purpose is continuity and correlation across one
evolving human/AI interaction—not execution sequencing. It should answer what
belongs to a session, which revisions and decisions relate to each other, and
what happened over time despite retries, failures, restarts, and changing
execution systems.

The intended ownership boundary is:

| Component | Responsibility |
| --- | --- |
| Hongxian | Session identity and lifecycle, opaque revision lineage, external-execution correlation, decision coordination, incidents and recovery records, projections, reconciliation contracts, audit queries, and operational-catalog abstractions |
| Siming | Cryptographically verifiable immutable evidence and ledger persistence |
| Zhinu or another engine | Workflow execution and sequencing; always optional to Hongxian |
| Application profile | Domain policy, recovery actions, artifact meaning, user explanations, and mappings to external systems |

Hongxian may define event envelopes and append/query ports, but must not
reimplement Siming's hash chain or claim ownership of cryptographic persistence.
It may record an application-invoked recovery handler's attempt and receipt,
but must not schedule recovery graphs, choose domain policy, or become a second
workflow engine. Actor authentication and authorization remain host-supplied
claims; Hongxian preserves and correlates them but cannot manufacture trust.

Guyabano retains workspace staging and promotion, generated-file ownership,
impact analysis, selective regeneration, coding evidence, and all Hetu,
Cangjie, Baize, and Zhinu policy. Hongxian sees only stable opaque references,
content identities, relationships, and application-defined event data. Large
artifacts remain in their authoritative stores.

Expected package shape after the boundary is proven:

```text
Penghou.Hongxian
    session, revision, decision, incident, projection, and reconciliation contracts

Penghou.Hongxian.Sqlite
    transactional operational catalog, projections, leases, and concurrency

Penghou.Hongxian.Zhinu
    optional workflow correlation and reconciliation adapter
```

Keep Siming behind a core ledger port. The first deployment may compose
`Penghou.Siming.Sqlite` inside `Penghou.Hongxian.Sqlite`; create a separate
Hongxian/Siming adapter package only if that keeps provider replacement and
dependency ownership materially cleaner.

Do not extract yet. First complete the four active session-hardening priorities
and exercise a realistic Guyabano failure, clarification, selective rerun,
process interruption, reconciliation, approval, and completion. The extraction
spike then must prove that a small application can create sessions and
revisions, attach arbitrary external operations, append application-defined
events, coordinate decisions, record recovery attempts and receipts, resume,
rebuild projections, reconcile incomplete work, and query history without
referencing Guyabano, workspace paths, generated files, Hetu, Cangjie, Baize,
or Zhinu types.

A Baize media-generation and batching profile is the intended second-consumer
validation. It should correlate batch members, attempts, variants, selections,
partial success, retries, and media lineage using generic artifact references
such as identity, content hash, media type, location, producer invocation, and
parent artifacts. Hongxian must not store image or video bytes or acquire
media-specific policy merely to satisfy this scenario.

Extraction exit criteria:

- The realistic Guyabano recovery scenario succeeds and reconstructs its audit
  history after process loss.
- The media-batch spike resumes partial work without regenerating acknowledged
  outputs and records variant selection and lineage without core API changes.
- The public kernel has no Guyabano or provider-specific types.
- Replacing the operational catalog or workflow adapter does not change
  application session policy.
- No Hongxian API performs general workflow scheduling or embeds domain recovery
  decisions.

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

## Phase 8 — Stabilize the extracted library

The preview extraction in Phase 5 is allowed to evolve. Consider a stable Luban
API only when all of the following are true:

- Baize and at least one substantially different executor are production-usable.
- The common contract has remained small and stable through real Guyabano tasks.
- Workspace fencing, cancellation, crash ambiguity, result limits, and evidence
  semantics are understood and covered by conformance tests.
- Capability descriptors describe observed differences rather than an imagined
  universal agent feature set.
- Extraction demonstrably removes provider coupling and can be consumed without
  referencing Guyabano.

A second application is strong validation but is not a prerequisite for an
experimental preview. Do not add general memory, planning, orchestration,
provider-native sessions, Git hosting, pull requests, distributed workers,
container infrastructure, or IDE abstractions to make the package appear more
general.

## Suggested internal structure

```text
Guyabano/
  Workflows/
    CodingWorkflow
    ArchitectureWorkflow
    ReviewWorkflow

  Coding/                    # temporary pre-extraction home
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

  Coding/A2A/
    A2ACodingExecutor
    A2ATaskReconciler
    A2AResultMapper

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

This is the pre-extraction responsibility map. Phase 5 moves only its proven
coding contracts and implementations into Luban; workspace lifecycle,
authoritative evidence collection, and workflow policy remain in Guyabano.

## Near-term implementation order

Zhinu `0.1.0-preview.11` is now consumed. Its authoritative restart receipt is
the restart authority, and request-bound signal receipts make ambiguous input
response retries safe. The active session-hardening scope is:

1. **Complete:** decision-bound approval integrity revalidates the persisted
   preview, binds workspace and Hetu revisions, holds a decision lease through
   restart acceptance, and separates authenticated approval from proposal.
2. **Complete for current rejection paths:** recovery success requires
   a verified action/resource receipt; denial abandons the exact candidate and
   stale workspace/impact decisions persist a replacement preview. Graph,
   staging, promotion, provider, cancellation, and timeout failures now enter
   the same forward-only incident model.
3. **Complete:** production session/CAS, decision leases,
   per-session Zhinu routing, cross-store operation state, and bounded runtime
   handles are concurrency-safe. Authoritative Siming appends are decoupled
   from rebuildable projections through durable committed/applied cursors,
   explicit lag diagnostics, and ledger-based background repair.
4. **Complete:** catalog lifecycle mutations, workspace promotion, and
   clarification promotion have durable retry-safe receipts. A persisted
   per-session/run cursor mirrors committed Zhinu events into Siming and routes
   terminal failures through recovery classification.

The session correctness boundary now includes structured operator-state
precedence, authoritative ledger commit time, bounded future occurrence claims,
and retry-safe Zhinu input responses. Resume deferred work:

5. Add operator query APIs and product-level interactive Zhinu
   request/wait/cancel/timeout/resume behavior.
6. Repeat real dogfood generation after the second-run structured-output and
   recovery-UX hardening; approve the focused decomposition restart, verify
   generation/build completion, then complete the store-level audit.
7. Run the `Penghou.Hongxian` extraction spike and validate the boundary with a
   Baize media-generation/batching profile before creating reusable packages.
8. Prove the minimal typed capability gateway with Codex-backed read-only web
   research and immutable session/workflow provenance.
9. Extract workflow phase collaborators without hiding the explicit Zhinu graph.
10. Add CI-server authorization, `.editorconfig`, and missing host/UI tests.
11. Resume executor/Luban extraction only after the session and workspace
   contracts are stable under real use.

Detailed session acceptance criteria and current implementation evidence live
in [`docs/session-backlog.md`](docs/session-backlog.md); do not duplicate those
checkboxes here.

Cross-repository ownership for rejection and recovery behavior is defined in
[`docs/rejection-recovery-ownership.md`](docs/rejection-recovery-ownership.md).

## Success measures

- Provider implementations can be replaced without changing Guyabano workflow code.
- No provider-specific types appear in the common executor contract.
- Build, test, review, and approval gates cannot be skipped by an executor.
- Actual workspace changes are independently observed and validated.
- Stale or ambiguous executions cannot silently corrupt a workspace.
- Process loss resumes without duplicating an acknowledged coding execution.
- Adding the second executor requires composition, not conditionals throughout
  Guyabano.
- The common abstraction remains small after real use by the Baize and A2A
  execution branches.
- Guyabano's methodology can later be represented as a validated Zhinu artifact.
- A session remains auditable and selectively rerunnable after UI, process, or
  transient chat history is lost.

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
| A2A coding task | Remote activity invocation and reconciliation |
| Review/fix loop | Deterministic quality gate and bounded loop |

Guyabano is the first vertical proving the compiled-workflow direction. Zhinu should
generalize only the runtime, capability, policy, and evidence concepts that Guyabano
demonstrates through real execution.
