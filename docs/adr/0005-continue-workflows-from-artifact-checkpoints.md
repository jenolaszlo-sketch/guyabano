# ADR-0005: Continue workflows from artifact checkpoints

- Status: Accepted
- Date: 2026-08-16

## Context

Code-generation workflows are deliberately split into expensive planning,
architecture, decomposition, generation, and verification stages. A late build
failure currently requires starting again from the original prompt, even when
the preceding artifacts are validated and the generated workspace is still
available. This wastes model tokens, time, and opportunities to debug a single
pipeline boundary in isolation.

Temporal retries are not a substitute for this behavior. A retry repeats an
activity within the same execution; a continuation creates a new execution
from durable, explicitly selected knowledge. Temporal history also has finite
retention and must not be Guyabano's long-term artifact store.

## Decision

Guyabano will support artifact-based workflow continuation, also called a workflow
fork.

1. A continuation always receives a new Temporal workflow ID.
2. The source workflow ID and continuation mode are recorded as lineage.
3. Guyabano persists an authoritative run checkpoint under the source workflow's
   `.gen/runs/{workflowId}` artifact tree.
4. Checkpoints contain the original prompt, merged workflow result, validated
   plan, decomposition results, generated-file provenance, and verification
   state available at the checkpoint boundary.
5. A continuation validates its checkpoint before skipping work. Missing or
   incompatible state fails explicitly; it never silently falls back to a full
   generation run.
6. The first supported mode is `BuildAndRepair`. It preserves the generated
   workspace, skips planning, review, decomposition, scaffolding, and initial
   generation, then runs the normal build and targeted repair loop.
7. For runs created before checkpoints existed, the caller may supply the
   completed Temporal result once as a migration fallback. Guyabano immediately
   writes that result as a durable checkpoint before continuing.
8. Loading a URL may select a source workflow, but must not itself start a new
   workflow. An explicit user action prevents duplicate executions caused by
   refreshes, crawlers, or repeated navigation.

## Consequences

### Positive

- Late failures can be reproduced and repaired without paying for successful
  model stages again.
- Continuations preserve auditability because source and child executions have
  distinct identities and recorded lineage.
- Checkpoints provide a foundation for retrying one node and invalidating only
  its downstream graph.
- Testing can focus on build and repair behavior using a known architecture.

### Negative

- Checkpoint schemas become compatibility boundaries and require validation and
  versioning.
- A build continuation assumes the generated workspace still corresponds to
  the checkpoint. Future work should add a workspace manifest and content-hash
  validation.
- Persisting merged results duplicates some data already present in smaller
  artifacts, trading storage for reliable and inexpensive recovery.

## Alternatives considered

### Restart from the original prompt

Rejected because it repeats validated work, burns tokens, and introduces new
model variability while debugging a late stage.

### Reuse the original Temporal workflow ID

Rejected because Temporal workflow identity and replay history must remain
coherent. A continuation is a new execution with explicit lineage.

### Depend only on Temporal history

Rejected because history retention is operational rather than an artifact
contract, and it couples durable Guyabano recovery to the workflow engine.

## Follow-up

- Add workspace file hashes to checkpoints.
- Add `RetryFailedTask`, `RerunNode`, and `RerunNodeAndDependents` modes.
- Invalidate downstream checkpoints using artifact dependency edges.
- Let the UI select a checkpoint and show its lineage and compatibility status.
