# Guyabano, Fuwen, and Qingniao integration plan

Date: **2026-09-20**. Status: **assessment and migration proposal; no code has moved**.

## Purpose

Guyabano currently integrates Fuwen directly for staged planning, workflow
authoring, admission, mutation, and Zhinu execution. Penghou.Qingniao now owns
the reusable lifecycle around one bounded delegated activity: provider
selection, idempotent acceptance, external-operation recovery, budgets,
supervision, candidate revisions, and normalized evidence.

This note records how Guyabano should consume Qingniao, which Guyabano code is
a candidate for later extraction, and which responsibilities must remain in
Guyabano or Fuwen. It is deliberately a plan rather than a decision to move
code immediately; the existing Fuwen path remains available for testing while
the Qingniao seam is proven.

## Current state

Guyabano has three related but distinct layers today:

1. `Guyabano.CodeGeneration.Planning` owns product-specific planning models,
   prompts, staged planning, workflow authoring, patches, and the bounded
   planning loop. Its `Fuwen` folder adapts Guyabano planning stages to Fuwen
   descriptors and execution ports.
2. `Guyabano.CodeGeneration.Workflows` owns the product workflow and its
   durable Zhinu step contracts. It also contains the original Fuwen parity
   pilot.
3. `Guyabano.WorkflowWorker` owns concrete model, filesystem, CI, artifact,
   session, and progress adapters for those steps.

The direct package references are therefore intentional consumer references:
Guyabano uses `Penghou.Fuwen`, `Penghou.Fuwen.Compiler`,
`Penghou.Fuwen.Zhinu`, and `Penghou.Zhinu`. Guyabano does not yet reference
`Penghou.Qingniao`.

Qingniao currently exposes stable contracts and reusable validation, identity,
provider, fingerprint, and policy components. Its full reference coordinator
is still internal and in-memory. It is not yet a public durable application
service that Guyabano can substitute for its production activity path without
additional adapter work.

## Target dependency direction

The target is composition, not nesting Fuwen inside Qingniao core:

```text
Guyabano product policy and prompts
    -> Fuwen source, compilation, admission, and immutable WorkflowPlan
    -> Zhinu durable workflow execution
    -> Guyabano Zhinu activity adapter
    -> Qingniao bounded delegation
    -> Baize, Codex, process, A2A, or deterministic provider
```

This preserves the existing authority boundaries:

| Owner | Responsibility |
| --- | --- |
| Fuwen | Workflow language, types, compilation, admission, plan identity, and workflow semantics |
| Zhinu | Durable scheduling, replay, retry, wait, restart, cancellation, persistence, and recovery |
| Qingniao | One bounded delegation, provider lifecycle, ambiguous-acceptance recovery, budgets, supervision, candidate identity, and normalized evidence |
| Guyabano | Software-development policy, planning prompts and artifacts, product workflow topology, repository context, CI policy, promotion, operator experience, and product-specific result mapping |
| Marang | Remote MCP/HTTP hosting, authentication, authorization, and operational composition |

Qingniao may accept an opaque `WorkflowPlanRevisionReference` and ask a host to
verify it. It must not parse Fuwen source, compile a plan, or interpret workflow
topology. A Fuwen plan selects and orders work; a Qingniao call performs one
delegated unit selected by that plan.

## What should not move

The following code is Guyabano product policy or Fuwen/Zhinu integration and
should not move into Qingniao core:

- `FuwenStagedPlanningService`, the planning stage definitions and executors,
  and the domain/topology/contract/component artifact models;
- `WorkflowAuthor`, workflow patching, preservation validation, and
  `PlanningLoop` decision policy;
- Guyabano prompt packs and Baize prompt construction;
- `CodeGenerationWorkflow` topology, step keys, architecture-review policy,
  decomposition rules, build-repair limits, and promotion policy;
- `ZhinuPlanningExecutionHost`, because its revision and fork behavior is
  coupled to Guyabano `WorkflowPatch` and planning-loop contracts;
- Guyabano session, progress, web-terminal, repository-context, CI, and
  artifact schemas;
- the Fuwen compiler or `Penghou.Fuwen.Zhinu` adapter itself.

Moving any of these into Qingniao would make a bounded delegation runtime own
software-development or workflow semantics and would invert the intended
dependency direction.

## Candidate integration and extraction seams

The first goal is consumption from Guyabano, not file movement. Introduce a
Guyabano-owned adapter at the leaf activity boundary and prove it against the
current behavior.

### Consume Qingniao from a generated-task activity

`CodeGenerationTaskActivities` is the best first vertical slice. It already
represents a bounded piece of work, has explicit context and token limits,
publishes artifacts and progress, and returns a product-specific
`CodeGenerationTaskWorkflowResult`.

Split that path into:

```text
Guyabano request/result mapping
    -> Qingniao delegation request
    -> provider execution and candidate evidence
    -> Guyabano artifact/progress/result mapping
```

Keep `CodeGenerationTaskWorkflowRequest`,
`CodeGenerationTaskWorkflowResult`, artifact publication, and progress events
in Guyabano. Qingniao types must not leak into persisted Guyabano workflow
contracts until versioning and replay compatibility are designed explicitly.

### Reuse Qingniao lifecycle semantics

The following concerns currently implemented in, or adjacent to, Guyabano
worker activities should converge on Qingniao rather than grow as a second
implementation:

- caller idempotency versus provider execution identity;
- provider selection by capability;
- external handle capture, observation, resume, and cancellation;
- safe provider retry versus semantic re-execution;
- bounded execution budgets;
- sealed candidate revisions;
- test and review evidence tied to the same candidate;
- terminal `Completed`, `Failed`, `Cancelled`, `BudgetExceeded`, and
  `NeedsSupervisor` outcomes;
- revision-fenced supervisor interventions;
- normalized provider and model provenance.

Guyabano classes such as `CodeGenerationRetryPolicy`,
`CodeGenerationModelSelector`, `CodeGenerationTokenBudgetSelector`,
`BaizeExecutionProvenanceRouter`, and the retry portions of
`CodeGenerationTaskActivities` should first become mappings onto these
Qingniao contracts. Delete or move code only after the mapping demonstrates
that the behavior is generic and no longer contains Guyabano policy.

### Possible later packages in the Qingniao repository

Do not create `Penghou.Qingniao.Fuwen`. The executable workflow seam is already
Fuwen to Zhinu, followed by an ordinary Qingniao activity call.

A future optional `Penghou.Qingniao.Zhinu` package is justified only after
Guyabano and Marang contain substantially identical glue. Such a package may
own reusable activity contracts, correlation mapping, cancellation
propagation, and result/evidence mapping. It must not own Fuwen compilation,
workflow topology, Guyabano artifact schemas, or Qingniao core policy.

Provider packages such as a Baize or Codex adapter may live in the Qingniao
repository when they are provider-neutral and reusable by both Guyabano and
Marang. Guyabano-specific prompts, file emission, CI commands, and workspace
promotion stay in Guyabano.

## Migration sequence

### Phase 0: keep the current Fuwen path green

- Continue testing `FuwenStagedPlanningService`, `WorkflowAuthor`, plan
  admission, mutation, fork/reuse, and the existing Fuwen pilot.
- Treat the hard-coded Guyabano workflow and its current activity results as
  the behavioral oracle.
- Record representative request, artifact, progress, retry, and failure
  fixtures before changing the runtime path.

### Phase 1: expose a usable Qingniao host seam

- Qingniao needs a supported coordinator/service entry point suitable for a
  real host; the internal in-memory reference coordinator is not enough.
- Define durable-state and Zhinu activity ownership without making Qingniao a
  workflow engine.
- Publish compatible Qingniao packages before Guyabano takes a package
  dependency.

### Phase 2: add a Guyabano adapter behind a selection flag

- Add a Guyabano-owned delegation adapter that maps one
  `CodeGenerationTaskWorkflowRequest` to Qingniao contracts.
- Use opaque workspace and artifact references; resolve paths only inside the
  trusted Guyabano host.
- Map Qingniao progress and terminal evidence back to existing Guyabano
  progress and result contracts.
- Keep the current direct activity path available for parity testing and
  rollback during the preview migration.

### Phase 3: prove one leaf task end to end

- Run a single generated-code leaf through Fuwen -> Zhinu -> Qingniao.
- Verify replay does not duplicate provider work after ambiguous acceptance.
- Verify a semantic retry creates a new generation while an observation retry
  remains on the same generation.
- Preserve Guyabano artifact identities, session correlation, and progress
  behavior.

### Phase 4: adopt candidate evaluation and supervision

- Route sealed candidate, deterministic test, independent review, evaluation,
  and bounded correction through Qingniao contracts.
- Map a Qingniao supervisor checkpoint into Guyabano's operator experience
  without making wake-up messages authoritative.
- Keep promotion, commit, push, and publication as separate explicit Guyabano
  capabilities.

### Phase 5: extract proven shared glue

- Compare Guyabano and Marang adapters.
- Move only byte-for-byte or semantically identical, product-neutral glue to
  optional Qingniao packages.
- Remove the old Guyabano lifecycle code only after replay, cancellation,
  evidence, budget, and focused-reexecution parity is demonstrated.

## Verification plan

The migration is complete only when both existing Fuwen behavior and the new
delegation boundary are covered.

1. **Fuwen tests** — compile/admit the same plans, preserve source maps and
   execution fingerprints, and retain revision/fork behavior.
2. **Mapping tests** — round-trip objective, criteria, constraints, workspace,
   budget, plan revision, artifacts, candidate identity, provenance, and
   terminal outcomes without leaking provider-specific types.
3. **Idempotency tests** — same request key plus same semantics returns the
   accepted delegation; conflicting reuse is rejected.
4. **Ambiguous-acceptance tests** — a lost start response reconnects through
   the captured provider handle and does not start duplicate work.
5. **Replay tests** — Zhinu replay observes or resumes the same Qingniao
   delegation and does not repeat completed side effects.
6. **Candidate tests** — test and review consume the same sealed revision;
   correction creates a new revision and preserves prior evidence.
7. **Budget and cancellation tests** — cancellation stops future work without
   claiming rollback, and exhausted budgets produce typed terminal results.
8. **Supervision tests** — stale checkpoint, revision, actor, and idempotency
   keys are rejected; accepted intervention resumes only dependent work.
9. **Parity tests** — old and new paths produce equivalent Guyabano outcomes,
   artifact relationships, progress milestones, and failure classifications
   for a fixed fixture suite.
10. **Safety tests** — no delegated provider gains commit, push, publish,
    credential, primary-checkout, or unrestricted filesystem authority.

## Decision gates

Before production switches to the Qingniao path, all of the following must be
true:

- Qingniao exposes a supported host entry point and durable recovery story;
- Fuwen and Zhinu versions used by Guyabano and Qingniao are compatible;
- one leaf-task vertical slice passes replay and ambiguous-acceptance tests;
- Guyabano persisted contracts remain readable across the change;
- old/new parity is demonstrated on successful, failed, cancelled,
  budget-exhausted, and supervisor-required executions;
- shared code is extracted only after a second consumer proves that it is not
  Guyabano product policy.

## Recommendation

Keep Fuwen as a peer capability and keep its current Guyabano integration for
planning and workflow testing. Add Qingniao beneath Zhinu leaf activities, not
around or above the Fuwen compiler. Move generic delegation lifecycle and
provider adapter code toward Qingniao only after the Guyabano consumer slice
proves the boundary; keep planning, workflow topology, artifact semantics, and
operator policy in Guyabano.
