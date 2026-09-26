# Fuwen pilot: reproducing the hard-coded code-generation flow

Date: **2026-09-15**. Status: **pilot proof — not a migration**.

## Purpose

Prove that Guyabano's hard-coded `CodeGenerationWorkflow` can be expressed
through Fuwen's compiler, admission, and Zhinu durable-execution seam without
changing workflow semantics, and record exactly which Fuwen features the full
migration still requires. This is the consumer evidence Delivery F asks for
before any old-path removal; the hard-coded workflow remains authoritative.

## What was built

All pilot code is additive and isolated from production workflow code:

- `src/Guyabano.CodeGeneration.Workflows/FuwenPilot/codegen-pilot.fuwen` —
  DSL source for the sequential spine:
  `context -> infer (planning) -> activity (scaffold) -> activity (build) -> return`.
- `src/Guyabano.CodeGeneration.Workflows/FuwenPilot/CodegenFuwenPilot.cs` —
  trusted catalogue, `CompileDslAsync`, programmatic IR v3 sequential plan,
  and programmatic IR v4 keyed fan-out plan mirroring the generation wave.
- `tests/Guyabano.WorkflowProgress.Tests/FuwenPilotParityTests.cs` — 3 tests:
  DSL/programmatic admission, durable sequential execution with replay, and
  fan-out source-order with selective single-item restart.
- `Guyabano.CodeGeneration.Workflows.csproj` — local `ProjectReference` to
  `Penghou.Fuwen`, `.Compiler`, `.Zhinu` (sibling repos; switch to package
  references once Fuwen `0.1.0-preview.2` is published).
- `Guyabano.WorkflowWorker.csproj` — `Penghou.Zhinu.Hosting`/`Sqlite`
  `0.1.0-preview.11 -> 12` (the Fuwen.Zhinu adapter requires Zhinu 12).

## Verification (2026-09-15)

- `dotnet build Guyabano.slnx --configuration Release`: `0 warnings, 0 errors`.
- `dotnet test Guyabano.slnx --configuration Release --no-build`: all suites
  pass — Artifacts 11, CI 17, CodeGeneration.Planning 80,
  CodeGeneration.Validation 8, Llm.CodeGeneration 17, Session 44,
  WorkflowProgress 204 (incl. 3 new pilot tests).
- Pilot proves: DSL compiles to the same sequential IR as the programmatic
  builder; admitted plans execute durably through `FuwenZhinuWorkflowFactory`
  on real SQLite Zhinu; replay reuses persisted envelopes without reinvoking
  providers; fan-out aggregates in source order and single-item restart
  re-executes only the invalidated item, preserving siblings — matching the
  hard-coded `Task.WhenAll` wave semantics in `CodeGenerationWorkflow.cs`.

## Hard-coded flow to Fuwen mapping

Source: `src/Guyabano.CodeGeneration.Workflows/CodeGenerationWorkflow.cs`
(1772 lines), `CodeGenerationWorkflowConstants.cs` (16 typed steps,
workflow `guyabano-code-generation` v7).

| Guyabano phase | Zhinu step keys | Fuwen equivalent | DSL text today? |
|---|---|---|---|
| `operation/start`, publications, completion, reconciliation | `operation/*` | `ActivityNode` (trusted `guyabano.*` descriptors) | Yes |
| Repository index/select/capture, reindex, checkpoints | `repository/*`, `checkpoint/*` | `ActivityNode` / `ContextNode` snapshot | Yes |
| Planning | `planning` | `InferenceNode` + typed context requirement (IR v3) | Yes |
| Architecture review loop (max 5 passes) | `architecture-review/{ver}/{pass}` | `InferenceNode` + control-only `if/else` | Partial (no value merge; loop unrolled) |
| Finding resolution sequence | `architecture-gap/*`, `architecture-integration/*` | Sequential `infer`/`activity` chain | Yes (sequential only) |
| Decomposition wave fan-out | `decomposition/{ver}/{parent}` via `Task.WhenAll` | IR v4 `FanOutNode` | No — programmatic only |
| Scaffolding | `scaffolding/{ver}` | `ActivityNode` (binding-derived `DependsOn`) | Yes |
| Generation wave fan-out | `generation/{parent}/{leaf}` via `Task.WhenAll` | IR v4 `FanOutNode` (item = leaf task) | No — programmatic only |
| Build + chained repairs (max 6 attempts) | `build/{n}`, `build-repair/...` | Sequential `activity` chain + `if` for no-progress | Partial (loop unrolled) |
| Approval / clarification gates | `RequiresUserInput`, restart preview approval | No interaction node | No |

## Gaps — all tracked in the Fuwen roadmap

Each gap below is already a Fuwen milestone; nothing found here is untracked:

1. **Keyed fan-out DSL surface** — IR v4 exists, grammar has no `fanout`
   production (`docs/fuwen-grammar.json`). Needed for decomposition and
   generation waves as authored source.
   Fuwen: **Milestone 8 — Parallelism and aggregation** (`roadmap.md:1040`),
   and Delivery F resume point ("Express one bounded Guyabano decomposition
   phase through IR v4 keyed fan-out", `roadmap.md:1127`).
2. **Bounded loops** — `for pass<=5`, build attempts, coherence re-review.
   Fuwen: **Milestone 10 — Explicit bounded correction loops**
   (`roadmap.md:1063`): named `repeat` with static max, declared loop state,
   typed `LoopLimitExceeded`.
3. **Value-producing conditionals** — `if(CanAccept) break` needs a merged
   output; current `ConditionalNode` is control-only.
   Fuwen: typed branch-result/merge contract (`roadmap.md:660,669`,
   `docs/zhinu-adapter.md:57`).
4. **Approval / clarification gates** — `RequiresUserInput`, restart-preview
   approval, supervisor checkpoints.
   Fuwen: **Milestone 9 — Waiting and interaction** (`roadmap.md:1053`):
   named waits on Zhinu idempotent signals, host intents for input/selection/
   approval; plus the P0 structured external-input node (`roadmap.md:402`).

## Non-goals of this pilot

- No change to `CodeGenerationWorkflow.cs` or any production step; the old
  path is not removed and no behavioral parity claim is made beyond the
  sequential spine and one fan-out shape.
- No dynamic wave scheduling: Fuwen fan-out requires a bounded list source
  with validated keys before child work, while Guyabano computes ready sets
  at runtime. Bridging that is Milestone 8 design work, not pilot scope.
- No approval-gate or revision-invalidation semantics (Milestones 9/10,
  immutable `PlanRevision` envelope).

## Next steps

1. In Fuwen, deliver the consumer-driven prep plan: fan-out DSL surface,
   value-producing conditionals, then bounded loops and interaction gates —
   sequenced so each unlocks one more Guyabano phase as authored source.
2. Keep the pilot green while that lands; extend it phase by phase.
3. Only after behavioral parity plus a focused-restart dogfood run, remove
   the old implementation (per Delivery F exit criteria).

## Wave 1a status (2026-09-26): decomposition parity corpus

Stages 1-4 DSL work has landed since this doc was written: `codegen-pilot.fuwen`
now exercises IR v3-v7 (sequential spine, conditional merge, repeat loop,
keyed fan-out, checkpoint, wait) and the parity suite covers compile, admission,
durable execution, restart scope, and signal resume.

Wave 1a adds the decomposition corpus in
`tests/Guyabano.WorkflowProgress.Tests/DecompositionParityTests.cs`:

- Corpus scheduling contract: `OrderCodeGenerationTasks` /
  `GetReadyCodeGenerationTasks` wave sequences locked for diamond, linear, and
  fan DAGs. Both paths share these functions: the host computes ready lists,
  Fuwen fan-out consumes a bounded list per wave.
- Scripted fan-out execution on SQLite Zhinu: source-order aggregation,
  fail-once-then-succeed infrastructure retry with per-item call counts, fatal
  failure surfacing as `WorkflowExecutionFailedException` with the provider
  diagnostic, and restart-mid-wave rerunning only the invalidated item.
- Recorded open dimensions before old-path removal: hard-coded
  `decomposition/{version}/{parent}` keys versus Fuwen content-hash runtime
  keys; run-failure diagnostics not naming the structural path; live
  differential execution of the full hard-coded workflow; waves 2-4
  (generation/review, build-repair/approval); focused-restart dogfood run.
