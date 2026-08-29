# Guyabano

Guyabano is an opinionated, deterministic software-development workflow for
.NET. It coordinates planning, architecture review, task decomposition,
implementation, build/test, correction, and validation phases using
[Zhinu](https://github.com/jenolaszlo-sketch/penghou-zhinu) for durable
workflow execution and [Baize](https://github.com/jenolaszlo-sketch/penghou-baize)
for model communication. [Hetu](https://github.com/jenolaszlo-sketch/penghou-hetu)
indexes repository structure, while
[Cangjie](https://github.com/jenolaszlo-sketch/penghou-cangjie) records the exact
source-derived context selected for a workflow.

> Guyabano decides what must happen. Zhinu durably enforces the process.
> A coding executor attempts one bounded workspace change.

## Packages

| Package | Purpose |
| --- | --- |
| `Guyabano.CodeGeneration.Planning` | Architecture review, domain discovery, decomposition, planning |
| `Guyabano.CodeGeneration.Workflows` | Durable Zhinu workflow orchestration |
| `Guyabano.CodeGeneration.Validation` | Generated file validation (CSharp/Json/Xml) |
| `Guyabano.Llm.CodeGeneration` | LLM-driven code emission and file management |
| `Guyabano.Llm.Prompting` | Prompt building and template engine (Scriban) |
| `Guyabano.Artifacts` | Artifact storage with integrity verification |
| `Guyabano.Session` | Long-lived session identity, event contracts, and projections |
| `Guyabano.Session.Sqlite` | Penghou.Siming-backed transactional session event ledger |
| `Guyabano.Messaging` | Workflow progress publishing/subscribing |
| `Guyabano.CI.Contracts` | Build/test/scaffold contracts |
| `Guyabano.CI.Server` | HTTP CI server (build, test, JetBrains analysis) |
| `Guyabano.CI.Client` | Typed client for the CI server |
| `Guyabano.WebTerminal` | Blazor web terminal UI |

## Durable workflow composition

The code-generation workflow keeps control flow, bounded loops, gates, and
result aggregation visible in `CodeGenerationWorkflow.RunAsync`. Each external
operation is a typed, keyed Zhinu workflow step implemented in
`Guyabano.WorkflowWorker`:

```text
CodeGenerationWorkflow
  -> IndexRepositoryStep
  -> SelectRepositoryContextStep
  -> CaptureRepositoryContextStep
  -> PlanCodeGenerationStep
  -> Review / resolve / integrate architecture steps
  -> DecomposeCodeGenerationTaskStep
  -> ScaffoldCodeGenerationStep
  -> GenerateCodeTaskStep
  -> BuildGeneratedCodeStep
  -> Load / save checkpoint steps
```

Zhinu resolves every execution attempt in a fresh DI scope. Completed-step
replay resolves no implementation, and the durable step key remains separate
from the keyed implementation identity. Shared typed step references bind each
registration and invocation to the same input/output contract at compile time.
Guyabano does not enable Zhinu
compensation for these steps because filesystem, model, CI, and artifact
operations do not yet have a truthful reversible contract.

Workflow definition version `4` adds session identity and cross-product state
correlation around the repository-intelligence steps.
Earlier histories remain distinct instead of being replayed against changed
execution bindings.

## Repository intelligence

Repository context is enabled by default for the configured output workspace.
Guyabano incrementally indexes it into a durable embedded Hetu graph, derives a
content-addressed workspace revision, selects a bounded public surface or
configured symbol neighborhoods, and stores the rendered observations in
Cangjie. An immutable Cangjie snapshot ID and its Hetu publication identity are
carried through workflow results, task requests, and checkpoints for exact
restart replay.

Guyabano uses Hetu's exact published index identity and a publication-bound
query view. If the repository is reindexed while context selection is running,
selection fails explicitly instead of combining facts from different graph
generations. Selected observations enter Cangjie as one atomic batch, and
deterministic snapshot retries reuse the original immutable snapshot without a
read-before-create race.

Hetu remains authoritative for current code structure. Cangjie stores only the
bounded textual observations selected for a workflow; Guyabano does not copy the
entire graph into memory.

The durable graph uses LadybugDB. Hosts must provide a matching native runtime;
on Windows, LadybugDB also requires the OpenSSL 3 runtime libraries described by
the Hetu package documentation. Advanced hosts may register their own
`HetuHost` before calling `AddGuyabanoCodeGeneration` to replace the default
filesystem provider, C# plugin, or Ladybug store.

```json
{
  "CodeGeneration": {
    "RepositoryContextEnabled": true,
    "RepositoryId": "repo:guyabano-generated",
    "RepositorySymbolSeeds": [],
    "IncludeRepositoryContextInPrompts": false,
    "RepositoryContextMaximumPromptCharacters": 40000
  }
}
```

Source-derived context is local-only by default. Set
`IncludeRepositoryContextInPrompts` to `true` only when the selected model route
is permitted to receive repository information. The character limit is applied
before disclosure, and prompt text labels the snapshot as untrusted reference
data rather than instructions.

## Session event ledger

Guyabano persists each session's authoritative event history through
`Penghou.Siming.Sqlite`. Every session has an independently verifiable,
contiguously ordered ledger at
`<OutputRoot>/.gen/sessions/{session-id}/session.db`.

Rebuildable current-state projections live in
`<OutputRoot>/.gen/session-catalog.db`. Projection failure cannot roll back or
erase an event; replay or a complete ledger scan repairs the projection. Each
projection cursor is bound to its ledger head hash as well as its sequence.

Every immutable event uses envelope schema v1 and records payload sensitivity.
Callers choose whether payload content is retained, replaced by a versioned
SHA-256 digest, or omitted before append. This makes disclosure and retention an
append-time decision; later redaction never rewrites the audit chain.

Unusual conditions are first-class session incidents. Detection, recovery plan,
each meaningful attempt, and its outcome are appended with causation and stable
idempotency. A successful repair returns the session to a safe operator state
without removing the incident from history.

Restart approval is bound to an exact persisted impact artifact. Guyabano
revalidates its workflow target, restart mode, canonical change-set hash,
workspace revision, and Hetu publication while holding a session-scoped
cross-process decision lease through Zhinu's authoritative restart receipt.
Workspace promotion and repository reindexing use the same lease contract.

## Status

Pre-release scaffolding. See [ROADMAP.md](ROADMAP.md) for product direction and
[docs/session-backlog.md](docs/session-backlog.md) for the active implementation
tracker.
