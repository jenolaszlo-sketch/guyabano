# ADR-0001: Replace semantic architecture retries with a map/reduce loop

- Status: Accepted
- Date: 2026-08-14

## Context

Architecture review and task decomposition expose missing decisions that single-pass generation often ignores, including validation limits, duplicate handling, idempotency, API error semantics, dependency contracts, and framework conventions.

Repeating the original architecture request does not give the model new evidence or a narrower objective. It can waste tokens, discard valid architecture work, produce inconsistent replacements, and encourage a model to silence a concern rather than resolve it.

Malformed JSON and schema violations are representation failures and may benefit from a focused retry. An unresolved architectural concern is instead a reasoning and information problem that should create explicit work.

## Decision

Guyabano will replace semantic architecture retries with a map/reduce resolution loop:

1. Architecture review or decomposition identifies focused findings.
2. Each finding becomes an immutable resolution work item with stable identity, affected architecture IDs, evidence, and constraints.
3. Independent findings are resolved concurrently by focused Temporal activities.
4. A resolver uses established domain, API, framework, security, and engineering practices to select a pragmatic default when one exists.
5. Each resolver produces a versioned artifact containing its decision, reasons, alternatives, consequences, affected IDs, and whether the decision is user-overridable.
6. A single decision-integration activity reduces the authoritative resolutions into a coherent architecture patch.
7. The amended architecture is reviewed again before it is accepted.
8. Accepted architecture artifacts retain the resolution artifacts as provenance inputs.

The resolver may request user input only when reasonable alternatives could materially change product purpose, legal or safety obligations, data ownership, money movement, destructive behavior, or an explicitly disputed user-visible requirement.

Representation failures remain local retries:

- Invalid JSON or schema output retries the same focused activity with repair diagnostics.
- A semantic architecture gap creates resolution work rather than repeating the original generation.
- Conflicting resolutions are sent to an integration or arbitration step.
- A genuine product ambiguity is returned to the user.

## Consequences

### Positive

- Valid upstream reasoning and artifacts are preserved.
- Models receive smaller, more precise questions with relevant constraints.
- Independent research and design questions can run in parallel.
- Previously implicit defaults become inspectable architecture decisions or notes.
- Token spending targets unresolved concerns rather than regenerating an entire plan.
- Resolution provenance supports selective reruns and downstream invalidation.
- Strict reviewers can expose omissions without permanently blocking ordinary engineering decisions.

### Negative

- The workflow contains more activities and artifact types.
- Multiple resolution calls may cost more than a successful simple generation.
- Parallel resolutions can conflict and therefore require a deterministic integration step.
- Retry budgets must be coordinated across Baize and Temporal to avoid multiplicative retries.
- Progress reporting must explain the difference between review, resolution, integration, and validation.

## Alternatives considered

### Retry the complete architecture request

Rejected because it discards successful work, repeats large prompts, and does not focus the model on the raised concern.

### Ask the user about every omitted detail

Rejected because most omissions have conventional, reversible defaults and would create unnecessary interaction.

### Allow the implementation model to decide locally

Rejected for cross-component or observable decisions because different implementation tasks could choose incompatible behavior and leave no architectural record.

### Use one decision-integration model without focused resolution

Retained only as the reduction step. It is insufficient as the sole reasoning step because unrelated findings compete for attention in one large prompt.

## Follow-up

- Evaluate grouping closely related findings to control activity and token overhead.
- Add explicit conflict detection between independently produced resolutions.
- Add an arbitration strategy for incompatible but individually valid decisions.
- Measure resolution quality, token use, and rerun frequency during Guyabano dogfooding.
