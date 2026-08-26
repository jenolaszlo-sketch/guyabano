# Architecture & quality review — findings

Reviewed: 2026-08, current working tree (pre-first-release scaffolding).
Read-only audit; no code changes accompany this document.
No "Solo" references found in source, tests, or documentation (only in
binary artifacts from prior Zhinu database runs).

Scope: all src projects (~17K lines across 15 project directories), tests
(~184 facts), CI configuration, and build infrastructure.

## Summary

Guyabano is an opinionated, deterministic software-development workflow that
coordinates planning, decomposition, implementation, review, and correction
phases using Zhinu for durable execution and Baize for model communication.
The architectural separation is sound: methodology in Guyabano, durable
enforcement in Zhinu, bounded implementation in ICodingExecutor. However,
the solution has significant infrastructure gaps — no version control, no CI,
no centralized build config — and a dominant god-class workflow that will
become unmaintainable as phases grow.

## A — Infrastructure gaps (critical)

### 1. Not a git repository

The project directory has no `.git` folder. No version control exists for
any of the ~17K lines of source code. This is the single highest-priority
issue: without git there is no history, no rollback, no collaboration,
no CI trigger.

### 2. No CI pipelines

No `.github/workflows` directory exists. Nothing validates builds, tests,
or formatting on push. Combined with finding #1, zero automated quality
gates exist today.

### 3. No Directory.Build.props

Each csproj independently manages TargetFramework, Nullable,
ImplicitUsings, and TreatWarningsAsErrors. There is no centralized build
configuration, versioning, or shared package reference management.
Nullable and ImplicitUsings are enabled but TreatWarningsAsErrors is not
set anywhere.

### 4. No .editorconfig

No style enforcement. `dotnet format` would produce massive churn on first
run.

### 5. Five empty project directories

`Guyabano`, `JetBrains.ToolsWorker`, `Tools.Streaming`,
`Tools.Streaming.Client`, and `Tools.Streaming.Server` have no `.csproj`
and no code files. Remove from the solution or scaffold properly.

## B — Architecture

### 6. CodeGenerationWorkflow.cs is a 1255-line god class

A single RunAsync method handles planning, architecture review passes,
decomposition, task implementation, build/test, correction loops, and
final validation — all inline with inline StepOptions configuration. As
phases grow this becomes unmaintainable.

Opportunity: extract phase methods (PlanAsync, ReviewArchitectureAsync,
DecomposeAsync, ImplementTasksAsync, BuildAndTestAsync, CorrectAsync),
or use a phase pipeline pattern where each phase is a separate class.

### 7. Heavy Baize coupling in Planning/Prompting layers

~60 files across CodeGeneration.Planning, Llm.Prompting, and WorkflowWorker
import Penghou.Baize. Planning and prompt logic cannot be tested or reused
without Baize's full dependency tree.

Opportunity: introduce thin abstraction interfaces (e.g., ILlmClient,
IPromptResponse) in Guyabano-owned contracts so Planning/Prompting depend
on those instead of concrete Baize types.

### 8. CI.Server has no authentication

Build/test/scaffold endpoints are exposed over HTTP without any auth
mechanism. Any network-accessible client can trigger arbitrary builds.

### 9. Generated code and Zhinu WAL files checked into src

`Guyabano.WebTerminal/generated/` contains generated source files and
Zhinu database WAL files alongside source code. Exclude via .gitignore
(once #1 is fixed).

## C — Correctness & robustness

### 10. Raw InvalidOperationException throughout the workflow

The workflow god class throws bare InvalidOperationException for phase
failures, missing results, and invariant violations. No typed exception
hierarchy exists for distinguishing recoverable vs terminal failures.

### 11. No cancellation checks between workflow sub-steps

A cancelled run may continue through expensive LLM calls before hitting
the next await point.

### 12. ConfigureAwait inconsistent

Only ~9 uses across ~17K lines. No policy established.

## D — Testing

### 13. Coverage concentrated in Planning/WorkflowWorker

184 facts across 62 test files cover Planning, Validation, WorkflowWorker,
Artifacts, and Llm paths well. CI.Server, WebTerminal, and Messaging have
no dedicated test project.

### 14. No end-to-end workflow lifecycle test

Individual phases have unit tests, but no test exercises the complete
plan → review → decompose → implement → build → correct → validate
pipeline against a stub executor.

## E — Usability

### 15. No README

No README.md explaining setup, configuration, or how to wire providers.

### 16. Magic settings keys scattered

Provider/model configuration uses string-keyed settings dictionaries;
keys are discovered only by reading code.

## Done well (preserve)

1. Clean boundary: Guyabano owns methodology, ICodingExecutor owns bounded
   implementation, Zhinu owns durable enforcement.
2. Deterministic plugin registration and bounded batch ingestion patterns.
3. Comprehensive validation pipeline (CSharp/Json/Xml syntax validators).
4. Privacy-safe diagnostics that never log source content.
5. Retry policies with exponential backoff configured per-phase.
6. Checkpoint/resume support for long-running workflows.
7. Token budget selection and cost calculation per model.
8. xunit.v3 + FluentAssertions testing infrastructure.

## Suggested priority

1. **Infrastructure**: git init, .gitignore, Directory.Build.props,
   .editorconfig, CI pipeline (#1–#4).
2. **God-class extraction**: split CodeGenerationWorkflow into phase
   methods (#6).
3. **Typed exceptions**: Guyabano exception hierarchy (#10).
4. **Baize decoupling**: Guyabano-owned abstractions (#7).
5. **Empty project cleanup** (#5).
6. **CI.Server auth** (#8).
7. **README** (#15).
