# Session and state integration backlog

## Purpose

This is the single detailed progress tracker for Guyabano sessions and the
Zhinu, Cangjie, Hetu, Baize, artifact, and workspace integrations around them.
The product direction remains in [`ROADMAP.md`](../ROADMAP.md), while accepted
architectural decisions remain in [`docs/adr`](adr/README.md).

Recovery ownership and proposed reusable Zhinu changes are maintained in
[`rejection-recovery-ownership.md`](rejection-recovery-ownership.md).

Update this file when implementation evidence changes. Do not use it for chat
handover notes, completed review narratives, or speculative feature lists.

## Current state

Last reviewed: **2026-08-30**

- The session foundation and consolidated integration work are on `main`; this
  checkpoint closes the remaining approval, audit-gap, recovery-routing, and
  revision-evidence work in the active session-hardening boundary.
- The first complete container dogfood run reached decomposition and stopped
  safely before file mutation. Its five integration findings are implemented
  and awaiting the next dogfood run: relationship-aware contract closure,
  product-outcome incidents, focused restart UX, collision-free Baize
  invocation publications with derived Cangjie snapshots, and deployment/UI
  precedence cleanup.
- The full solution passes **374 tests** after the second dogfood hardening
  batch.
- `git diff --check` passes with only existing LF-to-CRLF warnings.
- The P0 review defects are corrected: Baize streaming and overload coverage,
  invocation-specific Cangjie snapshots, task-scoped file ownership,
  first-staging baseline/path safety, and distinct workspace/Hetu revisions.
- Production session/CAS, decision leases, workflow routing, and cross-store
  operation state are now concurrency-safe SQLite. Each session owns bounded,
  evictable Zhinu and Siming handles; catalog mutations use a retry-safe outbox
  to immutable Siming evidence; clarification promotion is a deterministic
  forward-recovery operation. Siming commits are now independent from
  projection delivery, with explicit committed/applied cursors, bounded failure
  diagnostics, and ledger-discovered background repair.
- Recovery success now requires a verified, typed action receipt. Restart
  denial durably abandons the identified candidate; stale workspace and impact
  approvals produce persisted replacement previews, expose their IDs to the
  caller, and leave the session awaiting fresh approval. Executor failure is
  retained as reconciliation-required evidence rather than success.
- The pre-extraction correctness boundary is complete. Projections now retain
  structured pending inputs, approval previews, and active incidents; derive
  operator state from all active conditions by explicit severity precedence;
  and use ledger commit time as the audit clock while preserving bounded
  occurrence-time claims. Zhinu's idempotent signal receipt is integrated for
  retry-safe responses. The remaining interactive work is product-level input
  request/wait, cancellation, timeout, and resume policy.

## Active priority boundary

Complete these four session-hardening outcomes before beginning interactive
session product work or broader dogfood evaluation:

1. Decision-bound approval integrity, including authoritative persisted-preview
   validation and a workspace/Hetu decision lease.
2. Truthful recovery execution with concrete actions, verification, and durable
   receipts—or an explicit `UserActionRequired` outcome.
3. A concurrency-safe SQLite operational session catalog and projection-lag
   semantics.
4. Durable crash-gap reconciliation between Zhinu, Siming, Cangjie, workspace
   promotion, and other critical cross-store mutations.

Sections 5 and 6 remain accepted roadmap work but are deliberately deferred
until all four outcomes above meet their acceptance criteria.

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
- Session activity and saga receipts are append-only facts. Compensation and
  reconciliation append new facts; they never delete or rewrite evidence of a
  failed attempt.
- Zhinu workflow history is never rolled back. Recovery is a new forward workflow
  action correlated to the failed operation, so operators can reconstruct both
  the failure and the repair.
- `StageKey = taskId` is the stable generated-manifest lookup. Repair cycle,
  attempt, mutation, and producer revision are metadata.
- Manifest schema v2 owns operation-aware file history. Guyabano workspace
  revision and Hetu index identity are separate values.
- Artifact content identity uses canonical JSON hash contract `v2`, so a
  persisted `JsonElement` can be verified without its original CLR type. Legacy
  typed-serialization hashes remain `v1` and are not reinterpreted. Zhinu also
  records the SHA-256 of the exact immutable envelope bytes for file integrity.
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

- [x] Revisioned Cangjie decisions, evidence, knowledge, and infrastructure for
  invocation-specific ordered context snapshots.
- [x] Post-generation Hetu reindex, publication artifacts, and task-aware impact
  explanations.
- [x] Bounded Baize execution provenance without raw prompt/response storage,
  while retaining streaming and cancellation evidence.
- [x] Staging baseline fencing, validation, promotion, rollback, and safe
  mutation path validation.
- [x] Per-session Siming ledgers, durable timeline projection, consistency audit,
  and a canonical vertical scenario.

## Remaining work

Work in this order. Do not add more feature breadth before items 1–4 are proven.

### 1. Cross-store operation and reconciliation state machine

- [x] Add the package-level saga foundation: stable UUIDv7 operation identity,
  deterministic participant idempotency keys, durable atomic operation records,
  immutable participant receipts, append-only transition history, guarded
  transitions, and explicit reconciliation state.
- [x] Start and resume an operation from the Zhinu workflow and carry its
  operation ID through every concrete participant.
- [x] Add explicit Zhinu workflow-v6 steps for operation preparation, Hetu
  publication receipt, final-checkpoint receipt, and successful completion.
- [x] Append idempotent, hash-chained session events for operation preparation
  and transitions; retry changes neither the original event nor its sequence.
- [x] Distinguish safe unsuccessful product results from infrastructure
  exceptions: product rejection appends `workflow-failed`, incident, recovery
  plan, and `UserActionRequired` without poisoning the resumable operation;
  unhandled exceptions remain `ReconciliationRequired`.
- [x] Record immutable receipts and append-only session events for every typed
  artifact/Zhinu publication and revisioned Cangjie concept write.
- [x] Persist `Prepared`, `WorkspacePromoted`, `Published`, `Completed`, and
  `ReconciliationRequired` transitions from the real generation/promotion path.
- [x] Exercise the commit boundaries with staging CAS failure/retry, validation
  rollback, typed-artifact/Zhinu publication recovery, idempotent Cangjie and
  session-event retries, failed participant receipts, and terminal workflow
  failure reconciliation tests.
- [x] Repair safe incomplete transitions by idempotent replay and report a
  precise forward-only operator action when automatic repair is unsafe.
- [x] Recompute canonical logical content and exact envelope-file hashes during
  audit and enforce artifact-root
  containment rather than trusting embedded values.
- [x] Replace the filesystem operation prototype in production with normalized
  SQLite operation heads, immutable participant rows, append-only transition
  rows, optimistic versions, unique idempotency keys, and cross-process tests.

Acceptance: failures before and after promotion are distinguishable, retries are
idempotent, and recovery does not require guessing which store is authoritative.

### 2. Unified context assembly for every Baize invocation

- [x] Introduce one application-level context assembler used by planning,
  architecture, decomposition, generation, review, and repair.
- [x] Retrieve accepted session-scoped Cangjie decisions, clarifications,
  knowledge, and relevant prior evidence instead of merely recording a snapshot
  reference after the fact.
- [x] Query the exact Hetu publication bound to the workspace revision for the
  task's files, symbols, dependencies, and likely impact neighborhood.
- [x] Combine selected memory, graph observations, typed artifacts, and current
  workspace files under bounded selection/ranking and character-disclosure
  budgets, exact-revision freshness, and an explicit disclosure opt-in.
- [x] Persist the exact ordered non-empty selection before each Baize call and
  bind its snapshot, Hetu publication, prompt template, and workspace revision
  to execution provenance.
- [x] Add prompt-contract tests proving selected context is rendered, bounded,
  revision-correct, and absent when disclosure policy rejects it.

Acceptance: every Baize execution can answer both "what context was selected?"
and "which of that context was actually disclosed to the model?" Replaying the
recorded selection produces the same context bundle.

Implementation note: disclosure remains off by default because session memory
and repository content may be sent to an externally hosted Baize provider. When
`IncludeRepositoryContextInPrompts` is explicitly enabled, every invocation path
uses the same bounded untrusted-data envelope and records the selected Cangjie
snapshot plus exact Hetu/workspace revision in execution provenance.

### 3. Revision-bound impact, approval, and promotion

- [x] Bind impact analysis to one accepted workspace revision and explicit
  change set.
- [x] Give preview and approval UUIDv7 IDs and reject approval after the
  referenced workspace revision becomes stale.
- [x] Load the authoritative persisted preview during approval and reject any
  mismatch in preview ID, workflow run, target step, restart mode, workspace
  revision, Hetu publication, or canonical change-set hash. An approval DTO is
  evidence supplied to the command, not authority by itself.
- [x] Hold a session-scoped decision lease/CAS from the final workspace/Hetu
  revision recheck through Zhinu restart acceptance so promotion or reindexing
  cannot race an approved command. The current cross-process file-lock provider
  is replaceable through `ISessionDecisionLeaseProvider`; the SQLite catalog
  will take over this contract in priority 3.
- [x] Reject approval after the referenced Hetu graph revision becomes stale.
- [x] Source actor identity from authenticated host context; the WebTerminal
  adapter resolves a stable authenticated subject and the default application
  provider rejects when no trusted host identity exists.
- [x] Separate proposal from approval: production application services do not
  synthesize `Approved = true` from a free-form `approvedBy` string.
- [x] Bind build, test, review, Hetu publication, and promotion evidence to one
  workspace revision.

Acceptance: the approved graph/code revision is provably the revision restarted,
validated, and promoted.

### 3A. Forward-only incident recovery

- [x] Add typed incidents, recovery plans, attempts, and outcomes whose complete
  causation chain is appended to the session ledger.
- [x] Project operator state, open incidents, resolved incident count, and last
  incident reason without erasing successfully recovered history.
- [x] Return typed safe outcomes for user-denied restarts, stale workspace
  approvals, and unexpected Zhinu restart rejection.
- [x] Make approval replay idempotent so it neither duplicates incident evidence
  nor repeats a completed restart.
- [x] Publish and consume Zhinu `0.1.0-preview.10`: pass `ApprovalId` as
  `RestartStepOptions.OperationId`, use `RestartStepWithReceiptAsync`, persist
  its event sequence and fencing generation. Successful and ambiguous restart
  retries now use Zhinu's authoritative receipt rather than full-ledger
  inference; the remaining scan is limited to local denial/staleness outcomes
  until those commands receive a durable receipt/index.
- [x] Require a verified, typed action receipt before appending
  `RecoverySucceeded`. The receipt binds action, resource type and identity,
  verification statement, execution time, and cross-system references.
  Execution exceptions or invalid receipts finish as reconciliation-required
  evidence. Restart denial now durably abandons the exact candidate; stale
  workspace, tampered impact, and stale-Hetu decisions persist and return a
  replacement preview. A refreshed preview resolves the incident while the
  operator state correctly remains `AwaitingApproval`.
- [x] Integrate graph-staleness, staging validation, promotion CAS, downstream
  publication, provider failure, cancellation, and timeout rejection paths.
  Staging-owned failures execute or defer typed recovery directly; committed
  workflow terminal events are classified by the reconciliation worker.
- [x] Mirror committed Zhinu events into Siming with a durable per-session/run
  cursor
  and deterministic idempotency key so a crash after Zhinu commit but before
  session append repairs the audit gap automatically.
- [x] Add a durable audit outbox/receipt for critical Guyabano-owned mutations.
  Workspace revision promotion and its lifecycle receipt commit atomically in
  the SQLite catalog and are delivered independently to Siming.

Acceptance: every abnormal condition and every attempted recovery remains
visible after successful repair, while callers receive the safe revision,
plain-language explanation, and next state without inspecting databases.

### 4. Durable SQLite session events and projections

- [x] Add `Guyabano.Session.Sqlite` backed by `Penghou.Siming.Sqlite`, with one
  independently verifiable ledger per session, contiguous sequence allocation,
  append, idempotency, concurrency, and interrupted-write recovery.
- [x] Add bounded cursor-based timeline pages while retaining the compatibility
  full-read API for bounded internal uses.
- [x] Version event envelopes and classify payload sensitivity; append-time
  retention can keep content, keep only a versioned digest, or omit it without
  rewriting immutable history.
- [x] Persist rebuildable current-state projections covering timeline head,
  pending input, workflow, and current-workspace state; projection gaps fail
  explicitly, each projected sequence is bound to its ledger head hash, and a
  ledger scan can rebuild the catalog.
- [x] Make Siming the default session event store. No JSONL migration is needed
  because Guyabano has no retained user session data.
- [x] Remove the prototype JSONL implementation and run all session/workflow
  tests against the Siming adapter used in production.
- [x] Decouple authoritative ledger append from projection delivery. Once
  Siming commits an event, projection failure must surface as projection lag
  with a rebuild cursor—not as an ambiguous append failure that a caller might
  retry into a duplicate event.
- [x] Project pending approval/input and structured active incidents, then
  derive operator state from all active conditions by precedence. Resolving one
  incident must not hide a remaining `Corrupt` or `ReconciliationRequired`
  condition; `AwaitingApproval` must be reachable.
- [x] Use ledger `CommittedAt` as the primary audit time and preserve
  caller-supplied occurrence time as a claim with bounded skew validation.
  Delayed historical claims remain valid for mirroring and reconciliation;
  implausible future claims are rejected by configurable policy.

Acceptance: multiple processes append safely and timeline reads do not scan the
complete event history.

### 4A. Concurrency-safe operational session catalog

- [x] Replace mutable `session.json` records with a SQLite catalog that supports
  transactional versioned CAS across processes while retaining one independent
  Siming ledger file per session.
- [x] Enforce a unique workflow-run-to-session mapping. Attaching one run to a
  second session must fail deterministically; lookup must never depend on file
  enumeration order.
- [x] Route Zhinu through a session runtime/store factory so every session owns
  one `workflow.db` containing all of its workflow runs. Remove the global
  `zhinu.db`; do not create a database per run. Prove that separate sessions can
  execute concurrently and that restart, child-workflow, and recovery runs are
  reopened from the correct session store after process loss.
- [x] Use the SQLite catalog as the cross-process session decision-lease
  provider with renewable ownership tokens; retain the filesystem provider only
  as a compatibility/test implementation.
- [x] Persist session creation, workflow attachment, workspace revision, and
  decision-lease changes as idempotent lifecycle evidence or durable receipts
  that can be reconciled into the session ledger.
- [x] Move `FileSystemCrossStoreOperationStore` to concurrency-safe SQLite (or
  another provider implementing the same contract); retain the filesystem
  implementation only as a compatibility/test provider.
- [x] Route clarification promotion through a stable cross-store command:
  record the Cangjie receipt and append `clarification-promoted` with a
  deterministic idempotency key so a crash cannot leave unaudited memory or a
  retry-created revision.
- [x] Bound and evict idle per-session Zhinu runtime/store handles without
  disposing a runtime while an operation holds a lease.
- [x] Bound or evict per-session Siming ledger handles in long-lived hosts so
  opening many historical sessions does not create an unbounded cache.

Acceptance: two worker processes cannot lose a workspace/catalog update, one
workflow run has exactly one session, and every operational mutation is either
audited or deterministically discoverable for forward reconciliation.

### 5. Interactive session APIs and UI — deferred

The first shell should make project/session switching a primary interaction:

```text
┌─ projects / sessions ─┬─ workflow activity and graph ─────────────┐
│ create, search, resume │ running nodes, status, selected activity  │
│ recent and archived    ├───────────────────────────────────────────┤
│                        │ conversation, progress, evidence          │
│                        │                                           │
│                        ├───────────────────────────────────────────┤
│                        │ prompt / clarification composer           │
└────────────────────────┴───────────────────────────────────────────┘
```

Keep the workflow area resizable/collapsible so conversation remains the main
workspace. Later, selecting a workflow node should scope inspection and chat to
that activity, expose its inputs, outputs, provenance, incidents, and artifacts,
and offer policy-safe preview/adjust/rerun actions. The UI must consume catalog
and projection query APIs; it must never discover projects by opening every
session database or ask users to diagnose raw SQLite state.

- [ ] Query APIs for current workspace, latest task manifest, file owner, pending
  input, restart preview, paged timeline, and audit.
- [x] Deliver input responses through Zhinu `0.1.0-preview.11` with a stable
  request-bound signal ID, authoritative receipt, authenticated host actor,
  immutable `input-provided` evidence, conflicting-response rejection, and
  crash-gap retry tests.
- [ ] Add the product-level durable input request/wait, cancellation, timeout,
  and resume policy using the receipt-backed response path.
- [ ] Session naming, rename, list, resume, archive, and possibly branch.
- [ ] Project create/find/open APIs with stable project identity distinct from
  session, workflow definition, and workflow run identity.
- [ ] Operator states: `Healthy`, `Warning`, `ReconciliationRequired`, `Corrupt`.
- [ ] Disclosure preview before repository context is sent externally.

### 6. Real dogfood evidence and context-quality evaluation — active

- [x] Run a real generation with the current embedded stores. The first full
  run reached task decomposition, retained seven validated sibling
  decompositions, and rejected the final test-task decomposition before any
  workspace files were generated.
- [x] First container dogfood attempt exposed a lifecycle-ordering defect:
  Hetu indexed the stable session workspace before it existed. New,
  uninitialized sessions now provision that directory before workflow start;
  a missing workspace with an accepted revision is rejected with an actionable
  typed failure and is never silently recreated empty.
- [x] Planning recovered from duplicate model-produced names through an outer
  Zhinu retry, but leaked `Dictionary`'s duplicate-key exception and displayed
  two planning attempts. Staged validation now keeps the first value only for
  safe analysis, reports every duplicate as correction feedback, and lets the
  existing focused planning retry repair the artifact inside one activity.
- [x] Fix the five findings from the first full run:
  - dependency context now includes contracts declared by transitive tested
    and upstream tasks even when their decomposition leaves do not repeat them;
  - an unsuccessful Guyabano result records an immutable product incident and
    explicit recovery target instead of masquerading only as Zhinu completion;
  - the terminal offers preview-and-approve restart of the failed decomposition
    with dependent invalidation and sibling reuse, and successful restart closes
    the recovery incident with a verified Zhinu receipt;
  - every Baize invocation has a revision/attempt/ordinal publication identity,
    while decomposition derives a non-empty invocation snapshot from the exact
    repository Cangjie selection instead of attempting an empty snapshot;
  - environment variables and command-line settings override component JSON,
    and UI retries render as one logical activity with retained warning evidence.
- [x] Review the second dogfood run. Planning eventually succeeded after Nuwa
  repaired several malformed responses, but repeated repaired-shape mismatch,
  missing empty relationship arrays, and overlapping relationship semantics
  consumed most of the run. Decomposition then safely rejected a doubled
  `TASK-TASK-...` sibling identifier before generation.
- [x] Harden Guyabano at the host boundary found by that run:
  - staged relationship collections default to empty when omitted while core
    domain and planning collections remain schema-required;
  - staged parsing retains bounded Nuwa repair-shape diagnostics even when the
    response was not truncated;
  - unknown decomposition dependencies report the complete valid sibling-ID
    set and are never silently rewritten;
  - failure progress carries a stable privacy-safe fingerprint and bounded JSON
    paths, without storing raw prompt/response payloads;
  - focused retry visibly separates impact preview from approval and reports
    restart acceptance before new progress arrives; preview and approval now
    publish distinct progress activities, while a manually restarted step
    demotes its prior terminal error to retained warning evidence;
  - golden tests cover wrong-stage valid JSON, concatenated documents,
    repaired-but-mismatched output, omitted relationship collections, doubled
    task prefixes, recovery-state transitions, and fingerprint stability.
- [x] Assign malformed syntax recovery and repaired-but-mismatched candidate
  selection to Penghou.Nuwa; the observed corpus and acceptance rules are
  recorded in Nuwa's roadmap. Guyabano remains responsible for strict rejection,
  correction feedback, diagnostics, and bounded retry policy.
- [x] Inspect the next UI dogfood attempt (`01a0529a-c25a-78a9-9fa6-85627e1196c5`).
  It ran from the August 27 container image rather than the current source and
  therefore did not validate this batch. It safely stopped with two failed
  decompositions. The only recovery evidence was an invalidation preview at
  `decomposition/1/TASK-TODOAPICONTROLLER`; no approval or Zhinu restart was
  submitted.
- [x] Diagnose the following rebuilt-container failure at `repository/index`:
  Hetu Ladybug replay treated older completed runs as non-success terminal
  updates once a newer publication owned the single latest-state row. The fix
  belongs to Hetu and is implemented for `0.2.0-preview.3` with a
  two-successful-runs/reopen regression test. Guyabano now consumes that
  package; no adapter workaround was introduced.
- [x] Publish focused-recovery progress explicitly. Preview, restart accepted,
  restart applied, and restart failure now have visible progress activities;
  manually restarted task activity retains its prior failure as warning
  evidence instead of remaining falsely failed after success.
- [x] Diagnose workflow `01a052c1-8113-71be-b26c-cf0f8384a917`. The
  `TASK-TODOCONTRACTS` decomposition succeeded on attempt 2, but retained
  diagnostics obscured that outcome in the UI. Both DTO generation calls also
  succeeded; their ownership lookup exceeded Cangjie's 100-result query limit,
  and subsequent retries collided with immutable task-context artifacts before
  exhausting a model budget that did not match the configured tiers.
- [x] Harden that failure chain: successful retries now display an explicit
  `Succeeded after retry` state while preserving prior evidence; ownership
  queries stay within Cangjie's provider limit; the configured model-tier count
  is captured in workflow input and used by Zhinu's durable retry policy; and
  publication or
  reconciliation failures after successful model output return an auditable
  `ArtifactPublicationFailed` result instead of regenerating files. Workflow
  version 7 owns the new persisted retry contract.
- [ ] Replace the current `LastOrDefault` failed-decomposition selection with a
  UI list or deterministic recovery queue. A run can contain multiple failed
  decompositions, and retrying only the last one cannot make the overall result
  successful.
- [ ] Repeat the same generation and verify it passes decomposition, produces
  files, and can complete build/validation; deliberately force one decomposition
  rejection and exercise the focused retry from the UI.
- [ ] Clarify after success, preview and approve the cascade, selectively rerun,
  reuse siblings, validate staging, promote, reindex, and reconstruct history.
- [ ] Inspect SQLite/Ladybug/artifact/workspace state, not only return values.
- [ ] Compare context-disabled and context-enabled runs for correctness, repair
  rate, token use, irrelevant-context rate, and provenance completeness.
- [x] Update this tracker from the first full-run evidence.

## Package boundary

`Guyabano.Session` may own identity, lifecycle, event envelopes, causation and
correlation, pending-input and approval models, projections, and reconciliation
status. `Guyabano.Session.Sqlite` may own embedded persistence.

It must not own code-generation tasks, Zhinu workflow policy, Hetu queries,
Cangjie promotion rules, Baize requests, generated-file semantics, or host
filesystem layout. Those remain Guyabano application/integration concerns.

After priorities 1–4 and the realistic recovery dogfood scenario, evaluate
extracting the proven generic portion as `Penghou.Hongxian`. The reviewed
responsibility boundary, package shape, media-batching validation, and
extraction criteria live in the `Penghou.Hongxian` section of
[`ROADMAP.md`](../ROADMAP.md); this backlog continues to track implementation
inside Guyabano until extraction is justified.

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

- Is the initial project-to-session mapping permanently one-to-one, or should a
  project later contain multiple named sessions or branches? Keep the durable
  identities distinct until real usage answers this.
- Can one session span more than one logical repository?
- Which clarification cascades may apply automatically?
- What are the retention rules for conversation, model payloads, staging,
  backups, and diagnostics?
- Is signed audit evidence required, or is hash-chain evidence sufficient?
- What branching semantics are useful after selective regeneration is proven?
