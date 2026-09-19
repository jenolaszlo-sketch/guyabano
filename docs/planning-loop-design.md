# Guyabano Planning Loop Design

> Status (2026-09-19): design proposal for gap C of the progressive-planning
> assessment. No code yet. Builds on the gap B patch machinery
> (`docs/workflow-patch-design.md`).

## 1. Problem

Everything built so far mutates workflows, but nothing owns the mutation
cycle. Today each planning-to-execution transition is harness-driven: some
outer code decides to propose a patch, apply it, author from it, and execute
the result. There is no component that:

- decides *whether* another iteration is warranted;
- enforces how many iterations, nodes, mutations, or model calls may happen;
- records where the cycle stands so a restart resumes instead of repeating;
- terminates the cycle with a reason instead of trailing off.

Gap C gives the PLAN -> EXPAND -> EXECUTE -> EVIDENCE loop an owner and a
policy. This is §9 and §15 of the progressive-planning spec.

## 2. Principle

**The host owns the loop and the policy; the workflow owns the work.**

The loop driver is a host-side service, not workflow nodes. Rationale:

- Policy (§9: "bounded by host policy") is a host concern; putting bounds in
  workflow nodes would let the planned artifact negotiate its own limits.
- It avoids the §16 self-modifying-workflow ambiguity: the workflow never
  rewrites itself. The driver proposes candidates; Zhinu controls version
  transitions, exactly as gap B established.
- Each iteration's *work* (planning inference, execution fragments) still runs
  as ordinary durable workflow activity with artifacts. Only the *decision to
  iterate* lives outside.

Durability comes from a checkpoint artifact, not from the driver's memory:
a restarted driver reloads the checkpoint, verifies it against live state,
and resumes. That is the test 6 contract.

## 3. The loop

One iteration:

```text
observe  (catalog revisions, workflow execution state, prior checkpoint)
   ↓
decide   (planner inference proposes a PlanningDecision; validator admits it)
   ↓
act      expand/revise → propose patch → apply → author → preserve → mutate → execute
         validate      → re-run validators + evidence check, no structural change
         finish        → record reason, stop
   ↓
checkpoint (persist iteration state as an artifact revision)
```

The driver repeats until a `finish` decision is admitted or a policy bound is
hit. Hitting a bound ends the loop with a typed outcome
(`Exhausted/bound-name`), never an exception and never a silent stop.

### Observation signals

`fresh` means "live catalog heads the loop has not yet seen", not
"arrivals since bootstrap": a fresh run seeds nothing as seen, so iteration
one reports the whole live inventory. The loop's own checkpoint revisions are
excluded from fresh (bookkeeping, not planning knowledge).

Separately, the driver reports **superseded pins**: design-pinned revisions
(`RequiredArtifacts`, binding `ContextArtifacts`) the catalog has moved past,
as `kind/name@current supersedes pinned kind/name@pinned` entries. This is
the revise signal, and unlike arrival-freshness it works on bootstrap for
pre-existing changes as well as mid-run.

## 4. PlanningPolicy

Bounds, checked before every iteration and before every mutation:

| Bound | Kind | Rationale |
|---|---|---|
| `maxIterations` | hard | Caps total decide/act cycles. |
| `maxMutations` | hard | Caps applied Zhinu version transitions. |
| `maxWorkflowNodes` | hard | Refuses patches whose merged design exceeds it. |
| `maxPlanningDepth` | hard | Caps propose→repair chains and decision nesting. |
| `maxModelCalls` | hard | Caps LLM invocations; deterministic and meterable. |
| `maxConsecutiveNoOps` | hard | Anti-thrash: repeated no-change iterations finish the loop. |
| `maxTotalTokens` | best-effort | Enforced from reported `LlmUsage.TotalTokens`; providers report "when reported", so absence warns rather than blocks. Count bounds remain the guarantees. |

All bounds are host configuration, visible in the checkpoint, and reported in
the terminal outcome. Exceeding any bound is a normal typed planning outcome.

## 5. PlanningDecision

Decisions follow the same propose/validate split as gap B patches: a planner
inference proposes JSON, deterministic code admits it.

```csharp
public sealed record PlanningDecision
{
    // What the decision was based on (pins staleness like patch bases do).
    public required string DesignFingerprint { get; init; }
    public required IReadOnlyList<string> ArtifactRevisions { get; init; }
    public required string WorkflowVersion { get; init; }

    public required PlanningAction Action { get; init; } // expand|revise|validate|finish
    public required IReadOnlyList<string> MotivatingArtifacts { get; init; }
    public required string Rationale { get; init; }
    public string? FinishReason { get; init; }
}
```

Admission rules: the pinned basis must match live state (else re-observe);
`expand`/`revise` require non-empty motivating artifacts drawn from actual
catalog revisions; `finish` requires a reason referencing acceptance material;
`validate` requires nothing structural. A dedicated `planning-decision` prompt
pack renders the observation (design summary, fresh revisions, evidence
digest, budget remaining) for the decider inference.

## 6. PlanningCheckpoint

A `planning-checkpoint/<workflow>` artifact, revised every iteration:

```text
iteration, workflow id + version, design fingerprint,
consumed { model calls, mutations, tokens-reported },
consecutive no-ops, decision log (compact),
status: running | finished | exhausted | failed
```

Resume protocol: load checkpoint → confirm the design fingerprint still
matches the live design and the workflow version is the expected one →
continue at `observe`. Any mismatch is a typed `failed` outcome with the
divergence described, not a blind resume. This is what makes the driver
re-entrant across restarts.

`Iteration` counts completed acting cycles; terminal saves record the outcome
without advancing it.

## 7. Composition with existing pieces

| Loop step | Existing component |
|---|---|
| observe artifacts | `IPlanningArtifactCatalog` revisions + `InvalidateAsync` impact |
| propose patch | `WorkflowPatchProposer` |
| merge + validate | `StagedExecutionGraphBuilder.Apply` |
| author revision | `WorkflowAuthor.AuthorFromPatchAsync` |
| drift gate | `PatchPreservationValidator` |
| no-op detection | design fingerprint equality (§7 of the patch spec) |
| execute + evidence | Zhinu workflow execution state |
| budget metering | call/mutation counters (hard) + `LlmUsage` (best-effort) |

Gap C adds only: the driver, the policy, the decision record + pack, and the
checkpoint. No changes to Fuwen or Zhinu are anticipated.

## 8. Bootstrap

Iteration zero is the existing staged pipeline: domain → topology →
contracts → components → execution design → initial authoring → first
workflow version. The loop takes over from the first admitted revision:
it records the initial checkpoint and proceeds to `observe`. The fixed
pipeline is therefore the loop's bootstrap, not a rival to it — which is
exactly the migration path gap D (dynamic stages) will later generalize.

## 9. Termination

Done-ness is planner judgment, recorded and validated — not magic:

- The decider proposes `finish` with a reason grounded in acceptance
  artifacts; the validator checks shape, not truth.
- Deterministic backstops always apply: bounds (§4), consecutive no-ops,
  and "no stale artifacts + workflow complete" short-circuit to `finish`.
- Every terminal outcome names its cause: `finished/reason`,
  `exhausted/bound`, or `failed/divergence`.

## 10. Tests

- Scripted decider: expand → applied → no-op → finish. Assert exactly one
  mutation, preserved survivors, recorded finish reason.
- Budget exhaustion: `maxMutations: 1` with an always-expand decider; the
  second iteration is refused with a typed outcome, workflow untouched.
- Restart resume: checkpoint saved mid-cycle; a fresh driver resumes without
  repeating the applied mutation (test 6 shape).
- Anti-thrash: consecutive empty patches reach `maxConsecutiveNoOps` and the
  loop finishes instead of spinning.
- Stale decision: basis fingerprint mismatch forces re-observe, never a blind act.

## 11. Non-goals

- Dynamic stage selection stays in gap D; the loop drives the *existing*
  staged producers plus patch-based revision.
- Cost accounting beyond reported usage; token bounds stay best-effort.
- Human approval gates (ROADMAP-deferred; Fuwen `wait` nodes already provide
  the mechanism when needed).
