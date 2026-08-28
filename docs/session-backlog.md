# Session and state integration backlog

## Purpose

This is the single detailed progress tracker for Guyabano sessions and the
Zhinu, Cangjie, Hetu, Baize, artifact, and workspace integrations around them.
The product direction remains in [`ROADMAP.md`](../ROADMAP.md), while accepted
architectural decisions remain in [`docs/adr`](adr/README.md).

Update this file when implementation evidence changes. Do not use it for chat
handover notes, completed review narratives, or speculative feature lists.

## Current state

Last reviewed: **2026-08-28**

- The stable session foundation is committed on `main` in `a8118a9`.
- The working tree contains a broader uncommitted implementation of the items
  marked complete below. Preserve it until it is reviewed and committed.
- The full solution passes **296 tests**.
- `git diff --check` passes with only existing LF-to-CRLF warnings.
- The P0 review defects are corrected: Baize streaming and overload coverage,
  invocation-specific Cangjie snapshots, task-scoped file ownership,
  first-staging baseline/path safety, and distinct workspace/Hetu revisions.

Verification:

```powershell
dotnet test Guyabano.slnx --no-restore --verbosity quiet
git diff --check
```

## Decisions

- `SessionId` identifies one long-lived engineering effort across multiple
  Zhinu workflow runs. Session and Guyabano-created workflow IDs use UUIDv7.
- Explicit sequences remain authoritative for event ordering.
- The session correlates authoritative systems rather than copying their data:
  Zhinu owns workflow state; Cangjie semantic knowledge and snapshots; Hetu code
  facts; Baize model execution; typed artifacts large payloads; and the
  filesystem the current workspace.
- A stable session workspace is mutated through isolated staging and accepted
  promotion. A workflow run ID is not workspace identity.
- Conversation is audit history, not automatic memory. Accepted clarification
  is deliberately promoted into Cangjie.
- `StageKey = taskId` is the stable generated-manifest lookup. Repair cycle,
  attempt, mutation, and producer revision are metadata.
- Manifest schema v2 owns operation-aware file history. Guyabano workspace
  revision and Hetu index identity are separate values.
- Keep `Guyabano.Session` package-ready and independently testable, but do not
  publish it as a general-purpose public NuGet until another consumer proves a
  stable generic boundary. Penghou adapters remain in Guyabano.

## Completed implementation

### Session identity and correlation

- [x] Durable session identity and record with multiple workflow-run mappings.
- [x] Session-rooted workspace resolution.
- [x] Session correlation across workflow, artifact, Cangjie, Hetu, and every
  Baize planning/generation/review/repair path.

### Workflow and artifacts

- [x] Typed artifacts publish into Zhinu with producing-step provenance, content
  hash, schema, location, artifact ID, and session ID.
- [x] Planning, architecture, decomposition, task-context, checkpoint,
  generated-file, repository, validation, impact, restart, model-execution, and
  promotion payloads.
- [x] Explicit dependency graph, restart preview, selective invalidation, and
  sibling reuse tests.
- [x] Task-scoped manifest v2 operations, hashes, stale tracking, and ownership
  conflict rejection.

### Context, graph, provenance, and workspace

- [x] Revisioned Cangjie decisions, evidence, knowledge, and invocation-specific
  ordered context snapshots.
- [x] Post-generation Hetu reindex, publication artifacts, and task-aware impact
  explanations.
- [x] Bounded Baize execution provenance without raw prompt/response storage,
  while retaining streaming and cancellation evidence.
- [x] Staging baseline fencing, validation, promotion, rollback, and safe
  mutation path validation.
- [x] Filesystem session events, timeline projection, consistency audit, and a
  canonical vertical scenario as prototypes.

## Remaining work

Work in this order. Do not add more feature breadth before items 1–3 are proven.

### 1. Cross-store operation and reconciliation state machine

- [ ] Give each cross-store operation a stable UUIDv7 operation ID and
  idempotency key.
- [ ] Persist `Prepared`, `WorkspacePromoted`, `Published`, `Completed`, and
  `ReconciliationRequired` transitions.
- [ ] Fault-inject filesystem/session CAS, typed artifact, Zhinu, Cangjie, Hetu,
  and session-event failures.
- [ ] Repair safe incomplete transitions or report a precise operator action.
- [ ] Recompute artifact hashes during audit and enforce artifact-root
  containment rather than trusting embedded values.

Acceptance: failures before and after promotion are distinguishable, retries are
idempotent, and recovery does not require guessing which store is authoritative.

### 2. Durable SQLite session events and projections

- [ ] Add `Guyabano.Session.Sqlite` with atomic multi-process sequence allocation,
  append, idempotency, pagination, and interrupted-write recovery.
- [ ] Version event envelopes and classify/redact payloads by sensitivity and
  retention policy.
- [ ] Persist current-state, pending-input, timeline, approval, reconciliation,
  and current-workspace projections.
- [ ] Retire JSONL as the default after migration tests exist.

Acceptance: multiple processes append safely and timeline reads do not scan the
complete event history.

### 3. Revision-bound impact, approval, and promotion

- [ ] Bind impact analysis to one accepted workspace revision and explicit
  change set.
- [ ] Give preview and approval UUIDv7 IDs; reject approval after the referenced
  workspace or graph revision becomes stale.
- [ ] Source actor identity from authenticated host context.
- [ ] Bind build, test, review, Hetu publication, and promotion evidence to one
  workspace revision.

Acceptance: the approved graph/code revision is provably the revision restarted,
validated, and promoted.

### 4. Interactive session APIs and UI

- [ ] Query APIs for current workspace, latest task manifest, file owner, pending
  input, restart preview, paged timeline, and audit.
- [ ] Durable input request/response, cancellation, timeout, and resume via Zhinu
  signals.
- [ ] Session naming, rename, list, resume, archive, and possibly branch.
- [ ] Operator states: `Healthy`, `Warning`, `ReconciliationRequired`, `Corrupt`.
- [ ] Disclosure preview before repository context is sent externally.

### 5. Real dogfood evidence

- [ ] Run a real generation with the current embedded stores.
- [ ] Clarify after success, preview and approve the cascade, selectively rerun,
  reuse siblings, validate staging, promote, reindex, and reconstruct history.
- [ ] Inspect SQLite/Ladybug/artifact/workspace state, not only return values.
- [ ] Update this tracker from observed evidence.

## Package boundary

`Guyabano.Session` may own identity, lifecycle, event envelopes, causation and
correlation, pending-input and approval models, projections, and reconciliation
status. `Guyabano.Session.Sqlite` may own embedded persistence.

It must not own code-generation tasks, Zhinu workflow policy, Hetu queries,
Cangjie promotion rules, Baize requests, generated-file semantics, or host
filesystem layout. Those remain Guyabano application/integration concerns.

## Other engineering backlog

- Extract phase collaborators from the large `CodeGenerationWorkflow` without
  hiding the workflow graph or dependency declarations.
- Add authentication and authorization to CI-triggering HTTP endpoints.
- Add `.editorconfig` and converge formatting without unrelated churn.
- Replace raw invariant exceptions at public/operator boundaries with typed,
  actionable failures.
- Add dedicated WebTerminal, CI server, and messaging tests where behavior is
  currently covered only indirectly.

## Open product decisions

- Can one session span more than one logical repository?
- Which clarification cascades may apply automatically?
- What are the retention rules for conversation, model payloads, staging,
  backups, and diagnostics?
- Is signed audit evidence required, or is hash-chain evidence sufficient?
- What branching semantics are useful after selective regeneration is proven?
