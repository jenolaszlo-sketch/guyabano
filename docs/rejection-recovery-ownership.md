# Rejection and recovery ownership matrix

Last reviewed: **2026-08-29**

Reference implementations inspected:

- Guyabano current session-hardening implementation;
- Penghou.Zhinu `0.1.0-preview.10` release candidate (idempotent restart
  receipt implemented and locally validated);
- Penghou.Siming `9365ba1` (`0.1.0-preview.2`).

## Boundary rules

1. The component that owns authoritative state owns its invariants and atomic
   transition. Guyabano owns workspace, revision, approval, Hetu, and promotion
   policy. Zhinu owns workflow runs, steps, generations, signals, retries, and
   restart transactions. Siming owns immutable ordering and verification.
2. Cross-store recovery is forward-only. No component pretends to offer a
   distributed transaction.
3. Durable Zhinu events determine what committed in Zhinu. Siming records the
   session-level explanation and cross-system causation, but it is not a shadow
   workflow database.
4. A product state such as `AwaitingApproval` belongs to Guyabano. Zhinu should
   provide durable signals and typed step results, not acquire Guyabano-specific
   workflow statuses.
5. Add a Zhinu feature when Guyabano would otherwise infer commit state, inspect
   Zhinu tables, duplicate workflow state, or make a non-idempotent administrative
   mutation retry-safe using a different database.

## Repository placement

| Repository | Owns | Should expose to Guyabano | Must not own |
| --- | --- | --- | --- |
| **Guyabano** | Code-generation sessions, workspace candidates, approvals, product recovery policy, promotion, and the operator-facing explanation | One coherent session API and projections assembled from stable provider receipts | Generic workflow, ledger, memory, graph, or model-runtime infrastructure |
| **Penghou.Zhinu** | Durable workflow/step state, retries, leases, fencing, signals, cancellation, restart transactions, and durable workflow events | Typed retry-safe administrative commands and authoritative receipts/events | Guyabano workspace, approval, graph, or session statuses |
| **Penghou.Siming** | Append-only event ordering, canonical payload integrity, checkpoints, verification, and storage-provider contracts | Idempotent append/read/verify operations and exact corruption diagnostics | Workflow policy, sagas, projections, or cross-store rollback |
| **Penghou.Hetu** | Code-graph publication, immutable graph/repository revision identity, impact queries, and graph-store consistency | Stable publication receipts and revision-bound impact results | Whether a Guyabano approval is valid or a workspace may be promoted |
| **Penghou.Cangjie** | Stable logical memory concepts, immutable revisions, snapshot identity, and idempotent memory writes | Snapshot/write receipts that can be bound to a Baize call and safely retried | Session chronology or workflow recovery policy |
| **Penghou.Baize** | Model invocation execution, rendered-input/model/tool provenance, streaming behavior, usage, and provider errors | A complete invocation receipt/result with stable correlation supplied by the caller | Selecting session memory, graph context, or deciding how generation recovers |

Cross-cutting rule: a provider owns the atomicity and idempotency of mutations to
its own store. Guyabano owns the forward-only saga that composes those receipts
into a useful code-generation session. Siming records that composition; it does
not coordinate it.

## Scenario matrix

| Scenario | Safe result visible to the user | Guyabano responsibility | Zhinu responsibility | Siming responsibility | Decision |
| --- | --- | --- | --- | --- | --- |
| User denies a preview | Candidate is not applied; session returns to a ready/clarification state | Supersede approval, abandon/quarantine candidate, explain next action | None when no workflow mutation began | Record denial, incident, recovery, and resolution | Guyabano |
| Workspace revision changed after preview | Reject stale approval and produce a refreshed preview | Compare accepted revision, hold a session decision lease, regenerate impact | None; workspace revision is not Zhinu state | Record stale values and successful safe recovery | Guyabano |
| Hetu publication changed after preview | Reject stale approval and recompute impact | Compare exact repository/index publication under the same decision lease | None; graph identity is not Zhinu state | Record old/new graph identities | Guyabano |
| Approval replay after an ambiguous response | Return the original restart receipt without restarting again | Reuse stable approval/command ID and present prior outcome | Atomically deduplicate restart command and return committed receipt | Idempotently mirror session explanation | **New Zhinu feature Z1** |
| Zhinu restart rejects before commit | Workflow remains unchanged; return typed rejection/recovery state | Map typed failure to an incident and user action | Return a typed administrative mutation failure | Record failure and plan | Zhinu taxonomy + Guyabano policy |
| Process dies after Zhinu restart commit but before Siming append | Reconciliation discovers the committed restart and fills the audit gap | Run a durable-event reconciliation worker with a cursor | Persist command ID and restart receipt in the atomic `step-restarted` event | Idempotently accept the mirrored event | Z1 + Guyabano bridge; no distributed transaction |
| Build, test, or review rejects staging | Accepted workspace remains unchanged; bounded repair or clarification | Domain validation policy, fresh staging, repair limits, explanation | Durable step result/retry already exists | Record rejection and each repair attempt | Guyabano using existing Zhinu steps |
| Staging validation throws | Candidate is discarded/quarantined; accepted workspace is unchanged | Filesystem cleanup, candidate evidence, typed result | Persist step failure/retry if executed as a durable step | Record incident and cleanup outcome | Guyabano using existing Zhinu retry semantics |
| Promotion baseline/CAS conflict | Restore prior workspace, supersede candidate, refresh preview | Filesystem/session CAS and rollback verification | None; Zhinu cannot atomically validate external workspace state | Record conflict and verified rollback | Guyabano |
| Filesystem promotion succeeds but downstream publication fails | Keep accepted workspace; replay remaining participants | Cross-store operation receipts and participant-specific recovery | Durable workflow steps and stable step idempotency already exist | Record every participant failure/retry/outcome | Guyabano saga using existing Zhinu primitives |
| Step side effect succeeds but process dies before step completion | Replay without duplicating external effect | Pass Zhinu step idempotency key to Cangjie/Hetu/artifact participant | Stable step key, retry, lease, and fencing already exist | Record the eventual observed outcome | Existing Zhinu + idempotent providers |
| User-input signal send receives an ambiguous response | Repeating the same user response must not create another signal | Generate stable response/signal ID and show delivery state | Deduplicate optional signal command ID; conflicting reuse is rejected | Record user response and delivery correlation | **New Zhinu feature Z2** before interactive sessions |
| User-input wait times out | Return to `AwaitingInput`, retry, cancel, or close according to product policy | Decide timeout policy and explanation | Durable signal waits and timeouts already exist | Record timeout and chosen recovery | Guyabano using existing Zhinu signals |
| User cancels a workflow | Show cancelled safe revision and candidate disposition | Decide staging cleanup and session state | Durable idempotent `CancelAsync` already exists | Record request, Zhinu outcome, and cleanup | Existing Zhinu + Guyabano |
| Lease expires or stale worker attempts commit | Resume/fence automatically and retain warning history | Translate durable evidence into session incident only when useful | Lease recovery, generation fencing, and events already exist | Record unusual recovery if mirrored | Existing Zhinu |
| Workflow definition/version is unavailable on resume | Stop mutation and request operator/deployment action | Explain required version and preserve workspace | Typed definition-unavailable failure and durable run state | Record unresolved incident | Existing Zhinu; improve taxonomy only if details are insufficient |
| Siming append fails before a critical mutation | Do not start the mutation; retry audit append | Enforce ledger-first guard | No change | Typed append failure and idempotency already exist | Existing Siming + Guyabano |
| Siming append fails after a filesystem mutation | Preserve operation receipt and append missing incident later | Durable Guyabano participant receipt/outbox and reconciliation | Not a Zhinu concern unless mutation occurred in a Zhinu transaction | Accept reconstructed event idempotently | Guyabano outbox/receipt |
| Siming ledger verification fails | Halt mutation and show `Corrupt` | Operator state and recovery guidance | None | Verify chain/checkpoint and report exact failure | Existing Siming + Guyabano |
| Session projection update fails | Event remains authoritative; expose projection lag and rebuild | Persist a projection cursor, retry/rebuild independently, and never make the committed append appear unsuccessful | None | Immutable event remains committed | Guyabano projection hardening |

## Proposed Zhinu additions

### Z1. Idempotent administrative restart receipt — implemented for preview.10

Zhinu now provides `RestartStepOptions.OperationId`,
`RestartStepWithReceiptAsync`, `RestartReceipt`, a typed
`WorkflowOperationConflictException`, and the optional provider contract
`IIdempotentWorkflowRestartRepository`. SQLite persists the receipt with the
`step-restarted` event and restart transaction.

Implemented behavior:

- first request atomically applies invalidation, bumps fencing generation,
  creates revisions, and writes the event/receipt;
- identical retry returns the committed receipt without another restart;
- reuse with different target, mode, or material request fields throws a typed
  conflict;
- receipt exposes operation ID, durable event sequence, generation, target, mode,
  affected steps, actor, reason, and whether it was newly applied;
- provider conformance and SQLite integration tests cover conflicting reuse,
  concurrency, generation/event uniqueness, and reopen/process recovery.

Provider conformance and SQLite integration tests cover concurrent retry,
conflicting reuse, generation stability, event uniqueness, and reopen recovery.
Guyabano must consume the published package and remove its current Siming-history
check as the authority for restart idempotency.

### Z2. Idempotent signal send — required before interactive sessions

Keep additive signal semantics as the default, but allow a caller-supplied
`SignalId`/idempotency key. Identical retry returns the buffered/delivered signal
receipt; conflicting payload reuse is rejected. This prevents browser/network
retries from submitting the same clarification or approval twice.

### Z3. Complete typed administrative failure taxonomy — useful, not blocking Z1

Administrative APIs should distinguish not-found, invalid state, definition
unavailable, stale generation/lease, conflict, timeout, cancellation, and store
failure. Guyabano maps these types to recovery policy and must not parse messages
or catch `Exception` to infer commit state.

## Features that should not be added to Zhinu

- Guyabano workspace, approval, Hetu, manifest, or session operator states;
- a distributed transaction across Zhinu, Siming, Hetu, Cangjie, and files;
- automatic compensation for filesystem promotion without a genuine reversible
  contract;
- a generic saga abstraction extracted from one consumer;
- a Siming-specific publisher inside the core workflow runtime.

Zhinu already provides durable events and cursor-based inspection. Guyabano can
mirror those events into Siming with at-least-once delivery and deterministic
idempotency. A generic `Penghou.Zhinu.Siming` bridge should be considered only
after another consumer needs the same mapping.

## Guyabano implementation status derived from the matrix

Completed: decision-bound workspace/Hetu approval, authoritative persisted
preview validation, authenticated host actor resolution with reject-by-default
fallback, Zhinu restart receipts, verified recovery action receipts, the SQLite
operational catalog, append/projection separation, clarification receipts, a
durable Zhinu-to-Siming cursor, terminal rejection classification, and an
atomic workspace-promotion lifecycle receipt/outbox.

Remaining: project structured pending approval/input and all active incidents
with explicit precedence; validate claimed occurrence time against ledger commit
time; implement Zhinu Z2 before accepting interactive signals.

## Recommended sequence

1. Complete structured operator-state precedence and occurrence-time handling.
2. Prove the recovery and audit paths in a real generation run.
3. Implement Zhinu Z2 immediately before interactive input/approval APIs.
