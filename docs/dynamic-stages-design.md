# Guyabano Dynamic Planning Stages Design

> Status (2026-09-19): design proposal for gap D of the progressive-planning
> assessment. No code yet. Builds on the gap C loop
> (`docs/planning-loop-design.md`) and the gap B patch machinery
> (`docs/workflow-patch-design.md`).

## 1. Problem

Artifact production is hard-wired: `FuwenStagedPlanningService` always runs
domain → topology → per-context contracts+components → assemble, through four
hand-built stage executors and a bespoke `PhasedRunner`. Every objective pays
for the full software-architecture pipeline whether it needs it or not, and
no other shape (research → prototype → benchmark; schema → migration plan →
compatibility tests) is expressible.

The planner must determine which artifacts an objective requires (§10), and
the runner must execute that plan — not the other way around.

## 2. Principle

**Separate stage definitions from orchestration.** A stage definition says
*how* to produce one artifact kind (pack, inputs, validator, retries). A
stage plan says *which* to produce, in which order, for this objective. A
planner inference proposes the plan; deterministic code validates and runs
it. This is the same propose/validate split as patches (gap B) and decisions
(gap C).

## 3. StageDefinition

One built-in or custom artifact producer, as data:

```csharp
public sealed record PlanningStageDefinition
{
    public required string Id { get; init; }            // "contract-design"
    public required string ArtifactKind { get; init; }  // "contracts"
    public required string SystemPack { get; init; }    // pack name
    public required string UserPack { get; init; }
    public required IReadOnlyList<string> InputKinds { get; init; } // upstream kinds
    public required string OutputSchema { get; init; }  // validated shape
    public required int MaxAttempts { get; init; }
    public required string ModelProfile { get; init; }  // capability routing
}
```

Validation of stage output reuses the existing per-stage validators; only
their invocation becomes generic. The four current stages become four
definitions with byte-identical packs, inputs, and retry counts.

## 4. StagePlan

The planner's answer to "what must we know before acting", as JSON:

```csharp
public sealed record PlannedStage
{
    public required string StageId { get; init; }       // must be a known definition
    public required string Name { get; init; }          // instance name, e.g. "contracts/billing"
    public required IReadOnlyList<string> DependsOn { get; init; } // upstream instance names
    public required IReadOnlyList<string> InputArtifacts { get; init; } // pinned revisions consumed
}

public sealed record PlanningStagePlan
{
    public required IReadOnlyList<PlannedStage> Stages { get; init; }
    public required string Rationale { get; init; }
    public required PlanningStagePlanProvenance Provenance { get; init; }
}
```

Plans are provenance/version aware, because planning itself becomes
iterative and mutable:

```csharp
public sealed record PlanningStagePlanProvenance
{
    public required string ProducedBy { get; init; } // inference identity, e.g. model or decision id
    public required IReadOnlyList<string> InputRevisions { get; init; } // catalog revisions consumed
    public required string DefinitionCatalogueVersion { get; init; } // catalogue the plan was built against
}
```

Definitions live in a versioned catalogue (`sha256:planning-stages/v1:…`
over the definition set). Plan admission verifies the catalogue version
matches — a plan built against any other catalogue is stale and rejected,
the same discipline as patch bases and decision bases.

Admission rules: every stage id resolves to a known definition; the instance
graph is acyclic; every dependency names a produced instance; every consumed
input revision exists in the catalog; artifact-kind flow matches the
definitions' declared input kinds (a stage cannot consume a kind its
definition does not declare). Unknown ids, cycles, dangling edges, and
kind mismatches are rejected with repair feedback.

## 5. Generic runner

Replaces the hand-wired `PhasedRunner`: topologically order the admitted
plan, and per instance render inputs → run the definition's inference with
its retry loop → validate output → publish the artifact revision. The
retry-loop Fuwen pattern (`RetryLoop` + gap resolution + assess/apply)
already exists per stage; the runner parameterizes it by definition instead
of duplicating it four times.

The runner is stage-kind agnostic: it never mentions domains, topologies,
contracts, or components. All software-path knowledge lives in the four
built-in definitions.

## 6. Bootstrap

The first stage plan comes from a `planning-bootstrap` inference over the
goal — the §1 decomposition (`required artifacts, dependencies, unresolved
decisions, acceptance criteria, next activities`) rendered as a stage plan.
Validated like any other plan. When the goal matches no known shape, the
bootstrap may propose research/experiment stages first; planning for
uncertainty is planning too.

No default-to-software-path fallback: an empty or single-stage plan is legal,
and the loop (gap C) owns what happens when the plan proves insufficient.

## 7. Graph-derivation seam

`StagedExecutionGraphBuilder` knows about bounded contexts and contracts —
that knowledge must move, not vanish. Each stage definition declares an
optional deterministic graph-fragment rule (`IGraphFragmentBuilder`): given
its produced artifacts, emit execution steps + bindings. The generic
assembler concatenates fragments in dependency order plus the fixed
integrate/verify tail.

Artifact kinds without a fragment rule contribute nothing automatically;
the planner then shapes the graph explicitly through workflow patches
(gap B machinery as escape hatch). This keeps custom paths (benchmarks,
migration plans) usable on day one while the software path stays fully
derived.

## 8. Relationship to the loop

Gap C consumes artifacts; gap D produces them. The handoff: a loop decision
may carry stage invocations alongside (or instead of) a workflow delta —
`expand` can mean "produce these artifacts, then patch". Concretely, the
`PlanningDecision` gains an optional `ProduceStages` list of
`PlannedStage` entries, validated against known definitions and executed by
the generic runner before patch proposal. The loop itself is unchanged
otherwise: bounds, checkpoints, and outcomes all still apply.

## 9. Migration proof

`FuwenStagedPlanningService` becomes "built-in software stage plan +
generic runner". The proof is the existing suite: every staged-planning test
must pass byte-identical with zero behavior change. Only after that lands do
custom definitions get exercised.

## 10. Tests

- A non-software path (research notes → benchmark → decision) runs through
  the generic runner with a custom definition and publishes revisioned
  artifacts.
- Unknown stage ids, cyclic plans, dangling inputs, and kind mismatches are
  rejected with repair feedback.
- The built-in software path reproduces the current pipeline exactly
  (existing tests unchanged).
- A loop decision carrying stage invocations produces artifacts before the
  patch proposal runs.
- Fragment-less artifact kinds leave the graph untouched for explicit
  patching.

## 11. Non-goals

- Custom execution semantics per artifact kind: execution still flows through
  workflow patches and the existing executors. D covers production only.
- Automatic stage-definition synthesis: new kinds are registered by hosts,
  not invented by models (a model proposing an unknown stage id is
  rejected, not indulged).
- Human approval gates (still ROADMAP-deferred).
