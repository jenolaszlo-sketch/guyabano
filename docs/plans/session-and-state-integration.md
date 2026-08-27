# Guyabano Session and State Integration Plan

## Purpose

This document is the durable handoff for Guyabano's near-term dogfooding work.
Read it before resuming implementation if prior chat or coding-session context is
missing.

The goal is to make a Guyabano code-generation effort durable, selectively
rerunnable, and auditable across:

- Zhinu workflow execution and dependency state;
- Cangjie decisions, evidence, memories, and context snapshots;
- Hetu code-graph publications and impact analysis;
- Baize model routing, usage, and execution evidence;
- typed Guyabano artifacts and the generated workspace.

The long-lived product concept is a **Guyabano session**. A session owns one
evolving engineering effort and survives individual Zhinu workflow runs.

## Resume here

1. Inspect `git status` and preserve all existing changes.
2. Read the **Observed baseline**, **Decisions**, and **Implementation order**
   below.
3. Inspect the latest successful run in the embedded stores before changing
   persistence behavior.
4. Continue from the first unchecked milestone whose prerequisites are complete.
5. Update this document with new evidence, decisions, and checked milestones
   before ending a substantial implementation session.

Do not key permanent output, memory, or repository identity solely by a Zhinu
workflow run ID.

## Handover snapshot — 2026-08-27

The repository is intentionally dirty and the implementation described here is
not committed. **Do not reset, clean, checkout, or discard the working tree.**
Several essential additions are still untracked, including
`src/Guyabano.Session`, `CodeGenerationWorkspaceResolver.cs`,
`CodeGenerationZhinuIntegration.cs`, their tests, and this plan.

Milestone 0 is complete. The next implementation target is **Milestone 1 —
Authoritative Zhinu artifacts**, starting with the second unchecked item:

1. Inventory every existing artifact write and classify the missing domain
   publications: planning, architecture, decomposition, task context,
   generated-file manifest, checkpoint, repository publication, and validation
   evidence.
2. Add the missing typed artifacts through the existing bridge in
   `src/Guyabano.WorkflowWorker/CodeGenerationZhinuIntegration.cs`.
3. Ensure every publication carries content hash, type/schema version, location,
   session identity, Guyabano artifact ID, and producing-step provenance.
4. Add real Zhinu SQLite integration tests beside
   `tests/Guyabano.WorkflowProgress.Tests/ZhinuArtifactPublicationTests.cs`.
5. Mark checklist items complete only when the corresponding tests pass.

Current verification command:

```powershell
dotnet test Guyabano.slnx --no-restore --verbosity quiet
git diff --check
```

Latest result: all 252 tests passed. `git diff --check` passed with only Git's
existing LF-to-CRLF working-copy warnings.

## Observed baseline

The baseline audit was performed on 2026-08-27 against the successful Todo API
generation in:

```text
src/Guyabano.WebTerminal/generated/.gen
```

Successful Zhinu run:

```text
a301cb3c-6a9d-4dee-b4e6-94e1d18712e4
```

Observed state:

- Zhinu persisted one completed workflow-version-3 run.
- The run contained 33 completed steps and 68 lifecycle events.
- The final generated solution built successfully.
- `workflow_artifacts` contained zero rows.
- `workflow_step_dependencies` contained zero rows.
- Cangjie contained 32 context items, 37 relations, 158 tags, and one context
  snapshot.
- Cangjie content included architecture artifacts, 12 component-work contexts,
  12 task decompositions, two workflow checkpoints, and one Hetu repository
  summary.
- Most Cangjie entries were generic `artifact` items rather than stable
  decisions, evidence, knowledge, or revisioned engineering concepts.
- Hetu produced a repository publication and workspace/index identity before
  generation, but the promoted generated workspace was not reindexed after the
  successful build.
- Runtime state appeared under the WebTerminal working directory while another
  repository-level `generated` directory also existed. State-root resolution
  therefore needs an explicit, stable host configuration.

These observations establish that individual products persisted data, but the
cross-product correspondence required for selective regeneration and audit was
incomplete.

## Existing uncommitted direction

At the time this plan was written, the working tree already contained
uncommitted integration changes, including:

- `CodeGenerationZhinuIntegration.cs`, which publishes Guyabano artifact
  references and progress through the active Zhinu step context;
- a `CodeGenerationWorkspaceResolver` that isolates output by Zhinu workflow
  GUID;
- workflow version changed from 3 to 4;
- related worker, client, registration, and test changes.

Preserve and review these changes. The Zhinu artifact-publication direction is
useful. The workflow-GUID workspace layout conflicts with the session decision
below and should not become the permanent identity model.

Milestone 0 subsequently replaced the workflow-GUID workspace layout with a
session-rooted layout and a durable workflow-run-to-session association.

## Decisions

### Session identity comes before the full session feature

Introduce a deliberately thin session foundation before expanding persistence
wiring:

- stable `SessionId`;
- stable repository and workspace identity;
- session record;
- association between a session and one or more Zhinu run IDs;
- session identity carried through workflow requests, artifact metadata,
  Cangjie scopes/provenance, Hetu repository identity, and model-execution
  metadata.

Do not initially build conversation projections, interactive UI, or the complete
event system. The thin identity prevents workflow IDs from becoming accidental
permanent identifiers that later require migration.

New session IDs and Guyabano-created Zhinu workflow run IDs use standard UUIDv7
values via `Guid.CreateVersion7()`. Existing UUIDv4 records remain valid. UUIDv7
improves chronological readability and secondary-index locality, but explicit
database sequences remain authoritative for event ordering.

### Product ownership

- **Guyabano Session** owns the shared identity, lifecycle, correspondence,
  interaction history, and audit view.
- **Zhinu** owns durable workflow execution, steps, dependencies, retries,
  signals, restarts, and producing-step artifact provenance.
- **Cangjie** owns revisioned semantic knowledge, evidence, relations, retrieval,
  and immutable context snapshots.
- **Hetu** owns structural code facts, incremental repository publications,
  bounded graph queries, and impact analysis.
- **Baize** owns normalized model/provider execution, routing, usage, finish
  reasons, repair, and transport diagnostics.
- **Guyabano's typed artifact store** owns authoritative large planning and
  generation payloads.
- **The filesystem workspace** owns current source state; staging mutations are
  not current until validated and promoted.

The session correlates these systems by typed references. It does not duplicate
all payloads into one database.

### Workflow and artifact graphs stay explicit

Artifact publication and memory projection may live in a focused session/state
component. Zhinu step dependencies, workflow sequencing, restart policy,
approval gates, and completion decisions remain visible in Guyabano workflow
code.

### Persistent workspace with transactional staging

Use a stable session workspace rather than one permanent directory per run:

```text
generated/
  sessions/
    {session-id}/
      session.json
      workspace/
      staging/
        {mutation-id}/
      artifacts/
```

A mutation ID is temporary transaction identity, not a user-visible code-gen
run. Generate and validate in staging, then promote an accepted revision into
the session workspace.

### Conversation is not automatically memory

The future append-only session timeline records user, assistant, workflow,
model, tool, and CI actions. Guyabano deliberately promotes accepted
clarifications, decisions, evidence, and lessons into Cangjie. Raw chat messages
must not automatically become durable semantic memory.

## Implementation order

### Milestone 0 — Thin session foundation

- [x] Add `SessionId` and a small durable session record.
- [x] Associate every new Zhinu run with a session.
- [x] Establish stable session repository and workspace identities.
- [x] Carry `SessionId` through workflow input.
- [x] Carry `SessionId` through artifact, Cangjie, and Hetu metadata.
- [x] Carry `SessionId` through Baize task-generation metadata.
- [x] Carry `SessionId` through all remaining Baize planning, architecture,
  decomposition, review, and repair calls.
- [x] Add unit tests proving that multiple Zhinu runs resolve to one session.
- [x] Stop deriving permanent workspace identity from a Zhinu run GUID.

Implemented foundation:

- `Guyabano.Session` defines the stable identity, durable record, store contract,
  and atomic file-backed store.
- New runs create sessions; build-and-repair continuation reuses the source
  run's session. A legacy source run without a session is attached to a newly
  created session during continuation.
- Workspaces resolve to `sessions/{session-id}/workspace` on both host and CI
  paths.
- Artifact envelopes persist the session ID; Cangjie artifact and repository
  context records include it in metadata, tags, provenance, and snapshots; and
  task-generation Baize requests carry host-neutral session/run/step metadata
  that provider clients do not serialize on the wire.
- Every planning, decomposition, architecture-review, gap-resolution, and
  decision-integration activity establishes an async-local Baize correlation
  scope. Prompt builders merge its session, workflow-run, and step identities
  into host-neutral request metadata; nested retries inherit the scope and
  parallel activities remain isolated by async execution context.
- Session store tests pass 4/4, planning tests pass 70/70, and workflow
  integration tests pass 127/127 as of 2026-08-27.

Acceptance:

- Two workflow operations can address the same session workspace and repository
  while retaining distinct Zhinu run IDs.
- Existing workflow result lookup still uses the Zhinu run ID where appropriate.

### Milestone 1 — Authoritative Zhinu artifacts

- [x] Finish Zhinu artifact publication from existing artifact-producing steps.
- [ ] Publish planning, architecture, decomposition, task context, generated-file
  manifests, checkpoints, repository publications, and validation evidence.
- [ ] Include content hash, type/schema version, location, session identity, and
  Guyabano artifact ID.
- [ ] Define behavior when filesystem/Cangjie publication succeeds but Zhinu
  publication fails.
- [ ] Add idempotency and recovery tests.

Current proof:

- A real Zhinu SQLite workflow test writes through the Guyabano artifact bridge
  and verifies the durable workflow artifact has producing-step provenance,
  content hash, and session metadata.
- Generated-file manifests, post-generation Hetu publication artifacts, and
  validation-evidence artifacts remain future checklist items above; the bridge
  will publish them when those domain artifacts are introduced.

Acceptance:

- A successful run has non-empty `workflow_artifacts`.
- Every published reference resolves to an authoritative artifact and producing
  step revision.

### Milestone 2 — Explicit Zhinu dependency graph

- [ ] Declare dependencies with `StepOptions.DependsOn` or
  `WorkflowContext.DependsOn`.
- [ ] Cover architecture review/integration, decomposition waves, generation
  tasks, build/repair, repository indexing, and checkpoints.
- [ ] Test fan-out and fan-in topology.
- [ ] Test that unaffected sibling branches remain reusable.

Acceptance:

- A representative successful run has non-empty
  `workflow_step_dependencies`.
- Zhinu can preview the transitive subtree affected by restarting a selected
  planning or generation step.

### Milestone 3 — Restart preview and selective rerun

- [ ] Expose a Guyabano operation that requests a Zhinu restart preview.
- [ ] Present invalidated, rerun, and reusable steps separately.
- [ ] Add explicit approval before material mutation cascades.
- [ ] Exercise Zhinu restart fencing and stale-worker rejection.
- [ ] Add a vertical test that reruns one branch without recreating unrelated
  outputs.

Acceptance:

- Selective restart reuses unaffected completed steps and recomputes all required
  dependents.

### Milestone 4 — Per-task generated-file manifests

- [ ] Publish one immutable manifest per generation step revision.
- [ ] Record created, modified, deleted, and renamed paths.
- [ ] Record before/after hashes, logical task ownership, session, workspace
  revision, model execution, and producer step revision.
- [ ] Detect stale files previously owned by a regenerated task.

Acceptance:

- Guyabano can answer which step and task produced every generated source file.

### Milestone 5 — Revisioned Cangjie concepts

- [ ] Replace hash-only logical keys where a stable engineering concept exists.
- [ ] Store accepted architecture decisions as `Decision` items.
- [ ] Store build/test/review observations as `Evidence` items.
- [ ] Store reusable failure/repair lessons as revisioned knowledge.
- [ ] Use `supersedes`, `derived-from`, `supports`, and other explicit relations.
- [ ] Scope current concepts to the stable session/repository rather than only a
  workflow run.

Acceptance:

- Regenerating a concept produces deterministic history under one logical key;
  current lookup returns the latest revision while prior revisions remain
  auditable.

### Milestone 6 — Context snapshots for every Baize call

- [ ] Capture the exact ordered Cangjie selection supplied to each planning,
  architecture, decomposition, generation, review, and repair call.
- [ ] Record query identity, strategy/version, purpose, session, workflow step
  revision, workspace revision, and Hetu index identity.
- [ ] Reference snapshots from model-execution and produced-artifact records.
- [ ] Add bounded disclosure previews before sending repository context.

Acceptance:

- Every model-produced artifact can identify the exact persisted context that
  informed it.

### Milestone 7 — Post-generation Hetu publication

- [ ] Reindex the validated workspace after accepted mutations.
- [ ] Persist Hetu publication receipt, index run ID, deterministic index
  identity, source transitions, and diagnostics as Zhinu artifacts.
- [ ] Store a compact Cangjie summary linked to the publication.
- [ ] Bind build, test, and review evidence to the same workspace/code revision.
- [ ] Prove incremental indexing skips unchanged source units.

Acceptance:

- The session's current workspace revision resolves to one exact current Hetu
  publication and matching validation evidence.

### Milestone 8 — Hetu impact to task/step ownership

- [ ] Map generated paths and symbols to owning planning tasks and Zhinu steps.
- [ ] Query Hetu dependents for changed or clarified code concepts.
- [ ] Combine code impact with workflow dependencies without allowing graph
  heuristics to bypass mandatory gates.
- [ ] Persist the proposed impact and the applied restart plan for audit.

Acceptance:

- Guyabano can explain why each node is invalidated and distinguish workflow,
  artifact, and code-graph causes.

### Milestone 9 — Complete Baize execution provenance

- [ ] Persist provider, effective model, routing/fallback decision, purpose,
  prompt/template version, request/response hashes, token usage, latency, finish
  reason, structured-output repair, retry/rate-limit evidence, and diagnostics.
- [ ] Correlate each invocation with session, Zhinu step revision, Cangjie
  snapshot, Hetu publication, and output artifact.
- [ ] Keep sensitive/raw payloads out of bounded audit events unless explicitly
  allowed.

Acceptance:

- Model cost and behavior can be audited per session, workflow, step, task, and
  artifact.

### Milestone 10 — Stable workspace and transactional staging

- [x] Implement session-rooted workspace resolution.
- [ ] Create isolated staging mutations from an exact baseline revision.
- [ ] Build, test, review, and Hetu-index staging before promotion.
- [ ] Atomically or recoverably promote accepted changes.
- [ ] Retain or clean failed staging areas according to explicit policy.
- [ ] Prevent concurrent mutations from promoting over a changed baseline.

Acceptance:

- Failed or stale mutations cannot alter the session's current workspace.
- A successful promotion updates workspace, Hetu, evidence, and session
  references consistently or leaves a detectable reconciliation requirement.

### Milestone 11 — Full interactive and auditable session

- [ ] Add the append-only session event store and ordered event envelopes.
- [ ] Record actor, event type, timestamp, causation, correlation, and bounded
  cross-system references.
- [ ] Add user/assistant messages, durable input requests, Zhinu signals,
  clarifications, approvals, model/tool/CI activity, invalidation, reruns, and
  promotion events.
- [ ] Promote accepted clarification into Cangjie knowledge deliberately.
- [ ] Add projections for timeline, current session state, pending input, and
  audit navigation.
- [ ] Optionally hash-chain events for tamper evidence.

Acceptance:

- An operator can reconstruct who did what, when, why, with which context, and
  against which code/workflow revision after UI, process, or transient chat
  history is lost.

### Milestone 12 — Reconciliation and dogfood proof

- [ ] Add a session consistency audit spanning Zhinu, Cangjie, Hetu, typed
  artifacts, Baize evidence, and the workspace.
- [ ] Detect missing references, stale revisions, mismatched validation evidence,
  and incomplete cross-store publication.
- [ ] Run the canonical scenario: generate successfully, add a clarification,
  preview the cascade, approve it, rerun only affected nodes, reuse siblings,
  validate staging, promote, reindex, and reconstruct the audit timeline.

Acceptance:

- The canonical scenario passes with real embedded stores and remains a
  regression test for all four Penghou products.

## Suggested component boundary

Start with:

```text
Guyabano.Session
  identity and lifecycle
  commands and events
  cross-product reference types
  audit and reconciliation models

Guyabano.Session.Sqlite
  embedded event store and projections

Guyabano session/state integration inside the host
  Zhinu artifact and event bridge
  Cangjie memory and snapshot projector
  Hetu revision and impact projector
  Baize execution projector
```

Do not split one adapter package per Penghou product until tests demonstrate a
real boundary. Do not move workflow methodology or dependency declarations into
the session library.

## Open questions

- What user-facing identity or name creates a session, and can it be renamed?
- Does one session always map to one logical repository?
- Which clarification cascades require approval versus automatic application?
- What retention policy applies to raw conversation, model payloads, staging
  directories, and HTTP diagnostics?
- Which cross-store transition failures are retried automatically, and which
  place the session into a visible reconciliation-required state?
- Is event hash chaining required initially or after basic audit projections are
  proven?

These questions do not block Milestone 0. Record answers here as decisions when
they become necessary.
