# Guyabano

Guyabano is an opinionated, deterministic software-development workflow for
.NET. It coordinates planning, architecture review, task decomposition,
implementation, build/test, correction, and validation phases using
[Zhinu](https://github.com/jenolaszlo-sketch/penghou-zhinu) for durable
workflow execution and [Baize](https://github.com/jenolaszlo-sketch/penghou-baize)
for model communication.

> Guyabano decides what must happen. Zhinu durably enforces the process.
> A coding executor attempts one bounded workspace change.

## Packages

| Package | Purpose |
| --- | --- |
| `Guyabano.CodeGeneration.Planning` | Architecture review, domain discovery, decomposition, planning |
| `Guyabano.CodeGeneration.Workflows` | Durable Zhinu workflow orchestration |
| `Guyabano.CodeGeneration.Validation` | Generated file validation (CSharp/Json/Xml) |
| `Guyabano.Llm.CodeGeneration` | LLM-driven code emission and file management |
| `Guyabano.Llm.Prompting` | Prompt building and template engine (Scriban) |
| `Guyabano.Artifacts` | Artifact storage with integrity verification |
| `Guyabano.Messaging` | Workflow progress publishing/subscribing |
| `Guyabano.CI.Contracts` | Build/test/scaffold contracts |
| `Guyabano.CI.Server` | HTTP CI server (build, test, JetBrains analysis) |
| `Guyabano.CI.Client` | Typed client for the CI server |
| `Guyabano.WebTerminal` | Blazor web terminal UI |

## Durable workflow composition

The code-generation workflow keeps control flow, bounded loops, gates, and
result aggregation visible in `CodeGenerationWorkflow.RunAsync`. Each external
operation is a typed, keyed Zhinu workflow step implemented in
`Guyabano.WorkflowWorker`:

```text
CodeGenerationWorkflow
  -> PlanCodeGenerationStep
  -> Review / resolve / integrate architecture steps
  -> DecomposeCodeGenerationTaskStep
  -> ScaffoldCodeGenerationStep
  -> GenerateCodeTaskStep
  -> BuildGeneratedCodeStep
  -> Load / save checkpoint steps
```

Zhinu resolves every execution attempt in a fresh DI scope. Completed-step
replay resolves no implementation, and the durable step key remains separate
from the keyed implementation identity. Guyabano does not enable Zhinu
compensation for these steps because filesystem, model, CI, and artifact
operations do not yet have a truthful reversible contract.

Workflow definition version `2` introduces these durable implementation
identities. Version-1 histories remain distinct instead of being replayed
against a changed execution binding.

## Status

Pre-release scaffolding. See [ROADMAP.md](ROADMAP.md) and
[docs/architecture-review.md](docs/architecture-review.md).
