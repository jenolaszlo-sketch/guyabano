# ADR 0007: Replace Temporal and Redis with embedded Zhinu

## Status

Accepted

## Context

Guyabano used Temporal with PostgreSQL for durable workflow execution and Redis
Streams for progress delivery. This made a local coding agent depend on three
infrastructure services before it could execute a generation request.

Guyabano's workflow is already expressed as stable stages with explicit artifact
checkpoints. It does not require Temporal's full event-replay programming
model. The web terminal and workflow execution can also share one process, so
a cross-process progress broker is unnecessary.

## Decision

Guyabano hosts Penghou.Zhinu inside `Guyabano.WebTerminal` and persists workflow runs,
steps, retry state, delays, and outcomes in SQLite under the generated
artifact root.

Each former activity invocation is a Zhinu step with a stable semantic key.
Completed steps reconstruct their persisted results when a workflow resumes.
The former worker project remains temporarily as a code-generation hosting
module, but it is no longer an executable or a separate container.

UI progress uses a singleton replayable in-process hub. This removes Redis
without coupling authoritative workflow state to ephemeral notifications.
Zhinu's SQLite database remains the source of truth for execution.

Temporal, PostgreSQL, Redis, Temporal UI, and the separate workflow-worker
container are removed. Guyabano CI and Ollama remain separate capability services.

## Consequences

- Guyabano starts with substantially less infrastructure.
- Workflow state survives process restarts through SQLite.
- Step keys and input hashes become part of the durable compatibility surface.
- Activity retry heartbeat details are retained during an executing process;
  a restart reconstructs committed step results but does not restore an
  uncommitted activity heartbeat payload.
- Detailed UI progress can be replayed while the web process remains alive.
  Durable custom progress replay should move to a future Zhinu custom-event
  API rather than reintroducing a broker.
- Existing artifact-based workflow continuation remains independent of the
  workflow engine and can migrate older run identifiers.
