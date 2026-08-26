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

## Status

Pre-release scaffolding. See [ROADMAP.md](ROADMAP.md) and
[docs/architecture-review.md](docs/architecture-review.md).
