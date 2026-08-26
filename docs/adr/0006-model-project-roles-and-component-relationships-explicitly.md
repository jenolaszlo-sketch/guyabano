# ADR-0006: Model project roles and component relationships explicitly

- Status: Accepted
- Date: 2026-08-16

## Context

The staged planning graph previously represented every component-to-component relationship as a generic dependency. That made interface consumption indistinguishable from direct concrete coupling, DI registration, test coverage, and implementation of an inward-facing port. Deterministic assembly could consequently infer outward project references, false task cycles, or dependencies on a concrete adapter merely because another component consumed its interface.

Project `kind` also mixed two concerns: how `dotnet` should scaffold a project and what architectural responsibility the project owns. A `Library` kind does not say whether the project is a domain model, application layer, contracts assembly, or infrastructure adapter.

## Decision

Guyabano will model scaffolding shape and architecture separately.

Every planned project has an explicit role: `Domain`, `Application`, `Contracts`, `Adapter`, `CompositionRoot`, `Test`, or `Tooling`. Project dependency validation enforces inward dependency direction, prevents production projects from referencing tests, and reserves concrete adapter wiring for composition roots.

Every staged component classifies relationships using distinct fields:

- `definesContractNames` identifies the component that emits a frozen contract.
- `implementsPortNames` identifies concrete implementations of interface ports.
- `consumesContractNames` identifies public contracts used at compile time.
- `usesConcreteComponentNames` identifies unavoidable direct concrete coupling.
- `registersImplementationNames` identifies composition-root wiring.
- `testsComponentNames` identifies verification targets.

A contract must have exactly one defining component. Implemented ports must be interface contracts. Registration and test relationships are legal only in projects with matching roles. A target cannot be assigned multiple component relationship types, and production components cannot depend on test components.

Deterministic task and project assembly uses these types rather than heuristics. Contract consumption and port implementation add references to the contract-owning project. They do not add a dependency on a concrete implementation. Direct use, registration, and testing add explicit component/task and, when necessary, project dependencies.

## Consequences

### Positive

- Dependency inversion is represented directly instead of inferred from names.
- Cycle diagnostics reflect real compile-time and generation ordering relationships.
- Project topology can be linted before expensive decomposition and implementation.
- Prompts can explain a smaller, unambiguous set of choices to weaker models.
- Future graph projections and repair cascades can distinguish API changes, implementation changes, wiring changes, and test impact.

### Negative

- Planning schemas and prompts are larger.
- Existing staged artifacts using generic component dependencies are not compatible with the new schema and must be regenerated.
- Role rules encode architectural policy and may need explicit extension for uncommon topologies.
- Components that both define and implement a small local contract must declare both facts.

## Alternatives considered

### Keep a generic dependency and infer its meaning

Rejected because names and project placement are insufficient to distinguish interface consumption, DI wiring, concrete calls, and testing reliably.

### Infer architectural role from project kind

Rejected because the common `Library` scaffolding kind represents several different DDD and clean-architecture responsibilities.

### Require a separate project for every role

Rejected because bounded contexts and architectural roles do not automatically justify additional deployment or assembly boundaries. Small systems may combine compatible responsibilities while still declaring the dominant role and preserving valid dependency direction.

## Follow-up

- Role-aware graph projections are now persisted as `component-work-context` artifacts and used by decomposition prompts. Extend the same projection boundary to implementation and repair prompts.
- Persist relationship provenance so changed contracts invalidate only affected downstream artifacts.
- Consider configurable role policies for architectures that intentionally differ from Guyabano's defaults.
