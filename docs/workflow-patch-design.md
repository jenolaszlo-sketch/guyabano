# Guyabano Workflow Patch Design

> Status (2026-09-19): design proposal for gap B of the progressive-planning
> assessment. Gap C and gap D are sketched at the end. No code yet.

## 1. Problem

Today a planning mutation re-derives the whole execution design and asks the
model to re-emit the whole workflow:

- `StagedExecutionGraphBuilder.Build` reads *every* current artifact and returns
  a complete `PlannedExecutionDesign` (steps + bindings).
- `PlannedExecutionDesignSummary.Render` renders that complete design.
- `WorkflowAuthor.AuthorFromPlanAsync` sends it to the model, which emits a
  complete Fuwen DSL for the whole workflow.

Every mutation therefore reproposes unchanged work. That is the main source of
accidental drift: a model that paraphrases an untouched node can change its
fingerprint and invalidate durable work that should have been preserved. It is
also wasteful — the expensive model call grows with the workflow instead of
with the change.

## 2. Principle

Planning proposes a **delta** against a known base. Deterministic code merges,
validates, and applies the delta. The model never re-emits work it is not
changing.

This reuses the pattern already proven by `ArchitectureDecisionPatch` /
`ArchitectureDecisionPatchApplier`: a model-authored list of
replacements/additions, validated for scope, applied by code, then re-validated.

## 3. The patch artifact

`WorkflowPatch` is a planning artifact (key `workflow-patch/<name>`), stored and
revisioned by `IPlanningArtifactCatalog` like every other planning output.

```csharp
public sealed record WorkflowPatch
{
    // Identity of the design this patch was authored against.
    public required string BaseDesignFingerprint { get; init; }

    // Artifact revisions that motivated the delta (provenance / justification).
    public required IReadOnlyList<string> DerivedFromArtifacts { get; init; }

    // Why this change exists; surfaced to review and to the Fuwen author.
    public required string Rationale { get; init; }

    public required IReadOnlyList<PlannedExecutionStep> AddSteps { get; init; }
    public required IReadOnlyList<PlannedExecutionStep> ReplaceSteps { get; init; }
    public required IReadOnlyList<string> RemoveStepIds { get; init; }

    public required IReadOnlyList<PlannedNodeBinding> AddBindings { get; init; }
    public required IReadOnlyList<PlannedNodeBinding> ReplaceBindings { get; init; }

    // Edge rewiring for surviving steps, keyed by step id. Absent = unchanged.
    public required IReadOnlyList<StepDependencyEdit> DependencyEdits { get; init; }
}
```

`StepDependencyEdit` carries a step id and its new `DependsOn` list, so a
surviving node can gain or lose an edge without being replaced wholesale.

## 4. Base identity and staleness

A patch is only meaningful against the design it was authored against. We add:

```csharp
public static class StagedExecutionDesignIdentity
{
    // Stable hash over canonical JSON of graph + bindings (step order
    // normalized by dependency order, bindings keyed by step id).
    public static string Compute(PlannedExecutionDesign design);
}
```

`Apply` rejects a patch whose `BaseDesignFingerprint` does not equal the current
design's fingerprint. This is the same discipline as Fuwen's
`WorkflowPlanIdentity.ComputeExecutionFingerprint` and the catalog's
provenance: a stale candidate can never be applied to a newer workflow. A stale
patch is a typed outcome, not an exception — the planner can re-author against
the new base (see gap C).

## 5. Assembly

New entry point, parallel to `Build`:

```csharp
public static PlannedExecutionDesign Apply(
    PlannedExecutionDesign current,
    WorkflowPatch patch);
```

Validation (mirroring `ArchitectureDecisionPatchApplier`):

1. Base fingerprint matches.
2. Additions do not collide with surviving step ids; replacements/removals/
   dependency edits reference surviving ids.
3. Every added step has exactly one binding; removing a step removes its binding.
4. No surviving step depends on a removed step (removal must be explicit about
   its dependents, or be rejected).
5. The merged graph is acyclic and topologically orderable.
6. The result passes the same structural validation `Build` guarantees: every
   step has a binding, descriptor references are exact, artifact references
   resolve to current catalog revisions.

The applied result is a first-class `PlannedExecutionDesign`, rendered by the
existing `PlannedExecutionDesignSummary.Render` and published as
`execution-graph` / `bindings` revisions via
`PlannedExecutionDesignVersions`.

## 6. Stability invariant (the point of the whole design)

If a step is not mentioned by the patch, its `PlannedExecutionStep` and
`PlannedExecutionBinding` are copied unchanged. Because Fuwen node identity and
the execution fingerprint derive from those values, an untouched step produces a
byte-identical node and therefore an identical fingerprint. Zhinu then preserves
its durable result without rerunning it.

This is what makes "regenerate only the affected downstream region" (§8 of the
progressive-planning spec) a property of the assembler rather than a hope about
model behaviour. The existing mutation tests 1-6 already prove the Zhinu side;
the patch makes the Guyabano side deterministic.

### 6.1 Canonical, deterministic compilation (explicit requirement)

Full Fuwen regeneration from the merged design is acceptable only under this
standing requirement:

1. **The compile chain is a pure function.** DSL text -> `WorkflowPlan` ->
   execution fingerprint must depend on nothing but its inputs: no wall-clock,
   no randomness, no unordered-enumeration leakage into canonical bytes.
   (Current state: no `DateTime`/`Guid`/`GetHashCode` in the Fuwen
   core/compiler sources, and `WorkflowPlanIdentity` order-normalizes every
   collection before hashing. This must be locked with a regression test that
   compiles the same DSL twice and asserts identical fingerprints.)
2. **Admission verifies preservation.** The model still re-emits the full DSL,
   so paraphrase drift of unmentioned nodes is possible. The harness therefore
   compares per-node fingerprints for every step *not* mentioned in the patch
   against the prior admitted plan, using the same canonicalization the
   fingerprint uses. Any drift is a validation failure with repair feedback
   ("node X changed but is outside the patch; reproduce it verbatim"), not a
   silent mutation.
3. **The authoring pack instructs verbatim carryover** of unmentioned nodes.
   Pack instruction is the belt; the admission check is the suspenders.

Without 1-3, §6 would be aspirational. With them, it is enforced.

## 7. No-op detection

If `Apply` yields a design whose fingerprint equals the current one, no mutation
is generated. An empty or cosmetic patch is a recorded no-op, not a new
workflow version.

## 8. Where the patch comes from

A new planning inference step proposes the patch; it does not mutate anything.

- Input: current design summary (`PlannedExecutionDesignSummary.Render`), the
  changed artifact revisions and the impact set from
  `IPlanningArtifactCatalog.InvalidateAsync`, and the unresolved decisions.
- Output: JSON `WorkflowPatch`, parsed and validated by the same structured-JSON
  discipline as the existing planning stages
  (`StructuredPlanningStageParser`).
- Admission: the compiler/validator is the trust boundary; the model only
  proposes text.

A new `workflow-patch` prompt pack (system + user) is rendered for this step,
sibling to `planning-gap-resolution`.

## 9. Incremental Fuwen authoring (deferred)

After assembly we still re-emit the full Fuwen DSL from the merged design. That
is acceptable *because of §6 and §6.1*: unchanged nodes keep identical
fingerprints, the admission check rejects drift, and Zhinu reruns only the
delta. Model-level savings come from §3 (the planning model authors a delta,
not a plan).

Re-authoring only the new/changed region of the DSL is a later optimization. It
requires splitting the Fuwen author input into "stable prefix + delta", and is
explicitly out of scope for gap B.

## 10. Tests

Existing signal, reframed onto patches:

- Tests 1-6 author a `WorkflowPatch` instead of a full DSL and assert the same
  preservation/rerun outcomes, plus that every unmentioned step kept an
  identical fingerprint.
- New: stale base patch is rejected; empty patch is a no-op; removal with a
  surviving dependent is rejected; dependency edit introduces a cycle and is
  rejected; patch scope (added ids) does not overwrite survivors.
- Determinism: compiling the same DSL twice yields identical fingerprints
  (Fuwen-side regression locking §6.1.1).
- Drift rejection: a candidate that rewrites an unmentioned node fails
  admission with repair feedback (locks §6.1.2).

---

## Appendix: gap C and gap D (rough shape, not designed here)

### Gap C — bounded planner loop and policy

A `PlanningLoop` component owns PLAN -> EXPAND -> EXECUTE -> EVIDENCE and is
itself durable workflow activity (§15). Each iteration:

1. evaluates current artifacts + evidence;
2. decides the next action (expand / revise / validate / finish);
3. authors and applies a `WorkflowPatch` (gap B);
4. executes the introduced fragment and records evidence;
5. checkpoints loop state as a `planning-checkpoint` artifact.

A `PlanningPolicy` object bounds the loop: max workflow nodes, max planning
depth, max mutations, cost/token budget, termination criteria. Exceeding a bound
ends the loop with a typed outcome, not an exception. The loop must be
re-entrant after restart (test 6).

### Gap D — dynamic stages

Replace the fixed domain -> topology -> contracts -> components pipeline
(`PlanningDomainDiscoveryExecutor` / `PlanningTopologyExecutor` /
`PlanningContractExecutor` / `PlanningComponentExecutor`) with
planner-driven artifact production. The planner's structured result (§1
`PlanningResult`: required artifacts, dependencies, unresolved decisions,
acceptance criteria, next activities) names which producers run. The four
current stages become built-in stage definitions selected by the planner, not
a hardwired order. Depends on B (fragments) and C (loop) being stable.
