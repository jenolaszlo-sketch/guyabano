# Guyabano

[![CI](https://github.com/jenolaszlo-sketch/guyabano/actions/workflows/ci.yml/badge.svg)](https://github.com/jenolaszlo-sketch/guyabano/actions/workflows/ci.yml)
[![License](https://img.shields.io/github/license/jenolaszlo-sketch/guyabano)](LICENSE)

Guyabano is an opinionated, auditable software-development workflow for .NET.
It coordinates repository inspection, planning, architecture review,
decomposition, implementation, validation, build repair, and promotion through
explicit durable boundaries.

[Penghou.Zhinu](https://github.com/jenolaszlo-sketch/penghou-zhinu) persists and
recovers execution. [Penghou.Fuwen](https://github.com/jenolaszlo-sketch/penghou-fuwen)
defines typed planning workflows. [Penghou.Baize](https://github.com/jenolaszlo-sketch/penghou-baize)
routes model work. [Penghou.Hetu](https://github.com/jenolaszlo-sketch/penghou-hetu)
and [Penghou.Cangjie](https://github.com/jenolaszlo-sketch/penghou-cangjie)
provide reproducible repository context.

> Guyabano decides what must happen. Zhinu durably enforces the process. A
> coding executor attempts one bounded candidate-workspace change.

## System shape

```text
request + workspace
    -> session operation and repository snapshot
    -> staged planning and architecture review
    -> typed Fuwen plan and host admission
    -> durable Zhinu execution
    -> bounded code-generation tasks
    -> reindex, validate, build, and repair
    -> immutable artifacts, evidence, and session outcome
```

The main code-generation workflow is currently version `7`. It keeps control
flow, retry limits, review gates, repair loops, and result aggregation visible
in `CodeGenerationWorkflow.RunAsync`. External operations are keyed,
strongly-typed Zhinu steps resolved in fresh dependency-injection scopes.

Completed steps replay from durable state without resolving their
implementations. Filesystem, model, CI, and artifact operations do not claim
compensation because they do not yet have a truthful reversible contract.
Downstream side effects instead receive stable idempotency and correlation
identities.

## Packages and applications

| Project | Responsibility |
| --- | --- |
| `Guyabano.CodeGeneration.Planning` | Staged planning, architecture review, Fuwen authoring, workflow patches, and the bounded planning loop |
| `Guyabano.CodeGeneration.Workflows` | Durable Zhinu orchestration and workflow contracts |
| `Guyabano.CodeGeneration.Validation` | Generated C#, JSON, and XML validation |
| `Guyabano.Llm.CodeGeneration` | Model-driven code emission and candidate file management |
| `Guyabano.Llm.Prompting` | Scriban prompt packs and deterministic prompt construction |
| `Guyabano.Artifacts` | Immutable artifact storage with integrity verification |
| `Guyabano.Session` | Long-lived session identity, events, decisions, incidents, and projections |
| `Guyabano.Session.Sqlite` | Siming-backed transactional session event ledger |
| `Guyabano.Messaging` | Workflow progress publication and subscription |
| `Guyabano.CI.Contracts` | Build, test, and scaffold contracts |
| `Guyabano.CI.Server` | HTTP build/test and JetBrains-analysis service |
| `Guyabano.CI.Client` | Typed CI service client |
| `Guyabano.WorkflowWorker` | Hosted durable workflow worker and production adapters |
| `Guyabano.WebTerminal` | Blazor operator interface |
| `Guyabano.FuwenPlanning` | Executable Fuwen planning and authoring host |

Guyabano targets .NET 10.

## Durable code-generation workflow

The workflow records a cross-store session operation before product work begins,
then advances it alongside the authoritative Zhinu run. Its current path is:

```text
start session operation
  -> index repository -> select context -> capture immutable snapshot
  -> plan
  -> review / resolve / integrate architecture (bounded)
  -> decompose
  -> scaffold
  -> generate tasks
  -> reindex and checkpoint
  -> build / test / bounded repair
  -> record product outcome
```

Stable `WorkflowStepReference<TInput,TOutput>` values bind registrations and
invocations to the same contract. Retry counts, architecture passes, model
tiers, generation attempts, and build-repair cycles are hard-coded upper bounds
rather than open-ended agent loops.

A failed attempt preserves diagnostic artifacts and session history. Restart
approval is bound to the exact impact artifact, canonical change-set hash,
workspace revision, and Hetu publication, then applied through Zhinu's durable
restart receipt.

## Fuwen planning and workflow evolution

The planning subsystem has moved beyond whole-workflow regeneration.

The bootstrap path still runs the deterministic staged sequence—domain,
topology, contracts, components, execution design, and first Fuwen authoring.
The resulting source compiles through Fuwen IR v8 with workflow-owned prompts
and declared tools.

Later changes are represented as a `WorkflowPatch` against an exact
`BaseDesignFingerprint`. A patch names only additions, replacements, removals,
binding changes, and dependency edits. Deterministic code applies the delta,
rejects stale bases, missing bindings, dangling dependencies, and cycles, then
rebuilds the merged design.

`PatchPreservationValidator` compares every untouched node with the prior
admitted plan. If model-authored Fuwen source changes a node outside the patch,
admission fails with repair feedback. This turns “preserve completed work” into
a checked invariant instead of relying on prompt wording. Empty or cosmetic
patches are recorded as no-ops.

`PlanningLoop` now owns the bounded observe-decide-act-checkpoint cycle:

```text
observe current design, artifacts, evidence, and superseded pins
    -> admit expand / revise / validate / finish decision
    -> propose and apply a patch when required
    -> author, validate, preserve, mutate, and execute
    -> persist a planning checkpoint
```

Host-owned `PlanningPolicy` limits iterations, mutations, workflow nodes,
planning depth, model calls, consecutive no-ops, and reported token usage.
Exhaustion is a typed terminal outcome. A restarted loop loads its checkpoint,
verifies the live design and workflow version, and resumes without repeating an
applied mutation.

The first dynamic-stage slice is also implemented. A versioned catalogue
fingerprints stage definitions, the validator admits only known and acyclic
plans with resolvable inputs and valid artifact-kind flow, and the generic
runner executes admitted stages in topological order. It chains outputs and
publishes revisioned artifacts with provenance. Built-in domain, topology,
contract, and component stages mirror the established pipeline.

Planner-authored stage selection and integration into the end-to-end product
remain follow-on work. The built-in definitions preserve the current bootstrap
while the generic runner is proven.

Design details:

- [Workflow patch design](docs/workflow-patch-design.md)
- [Planning loop design](docs/planning-loop-design.md)
- [Dynamic stages design](docs/dynamic-stages-design.md)
- [Product roadmap](ROADMAP.md)

## Repository intelligence

Repository context is enabled by default for the configured output workspace.
Guyabano incrementally indexes the workspace into an embedded Hetu graph,
derives a content-addressed revision, selects a bounded public surface or
configured symbol neighborhoods, and stores the rendered observations in
Cangjie.

The immutable Cangjie snapshot and exact Hetu publication identity flow through
planning requests, workflow results, and checkpoints. Selection fails if the
graph is republished mid-query, preventing observations from different graph
generations from being combined.

Hetu remains authoritative for current code structure. Cangjie stores only the
bounded observations selected for the workflow.

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

Source-derived context stays local by default. Enable
`IncludeRepositoryContextInPrompts` only for a model route permitted to receive
repository information. The character limit is applied before disclosure, and
the prompt labels the snapshot as untrusted reference data.

The default graph store uses LadybugDB. Hosts must supply its native runtime;
Windows also requires the OpenSSL 3 runtime libraries documented by Hetu.
Advanced hosts can register their own `HetuHost` before
`AddGuyabanoCodeGeneration`.

## Session ledger and recovery

Every session has an independently verifiable, ordered event ledger at:

```text
<OutputRoot>/.gen/sessions/{session-id}/session.db
```

Rebuildable projections live at:

```text
<OutputRoot>/.gen/session-catalog.db
```

`Penghou.Siming.Sqlite` provides the append-only ledger. Projection failure
cannot roll back or erase an event; replay repairs the projection, and every
cursor is bound to its ledger sequence and head hash.

Events use envelope schema v1 and record payload sensitivity. Callers decide at
append time whether content is retained, replaced by a versioned SHA-256 digest,
or omitted. Incidents, recovery plans, attempts, and outcomes remain in history
after repair.

Workspace promotion, repository reindexing, and restart decisions use a
session-scoped cross-process lease. Failures that cross store boundaries become
discoverable reconciliation work rather than an implied distributed
transaction.

## Safety boundaries

- Coding happens in an isolated candidate workspace.
- Model output proposes plans, patches, source, and files; deterministic
  validation decides what is admitted.
- Exact descriptor, artifact, workspace, plan, and publication identities are
  carried through durable state.
- Repository context is not disclosed to model routes unless explicitly
  enabled.
- Git commit, push, package publication, and deployment remain explicit host or
  operator capabilities.
- External side effects must use stable idempotency keys or return durable
  receipts.

## Development

```powershell
dotnet build Guyabano.slnx --configuration Release
dotnet test Guyabano.slnx --configuration Release --no-build
```

The repository is active pre-release software. The durable code-generation
workflow and planning primitives are implemented and tested, while the
Fuwen-driven adaptive-planning path is still being integrated into the full
product surface.

## License

[AGPL-3.0](LICENSE)

Copyright (c) 2026 Jenő Konrád László
