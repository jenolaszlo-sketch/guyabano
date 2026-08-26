# ADR-0003: Resolve architecture findings sequentially with shared practices

- Status: Accepted
- Date: 2026-08-14

## Context

ADR-0001 introduced focused map/reduce resolution and ADR-0002 constrained decision integration. Resolving all findings in parallel still allows independent model calls to select incompatible conventions. A later integration call then needs the complete resolution wave and must consolidate decisions it did not make.

The workflow also lacked a separate representation for reusable engineering guidance. Decisions were recorded as project ADRs, but later resolvers could not intentionally reuse an established practice or a convention selected earlier in the same run.

## Decision

Resolve architecture findings sequentially within an architecture review pass.

For each finding, Guyabano will:

1. Supply the current architecture, the focused finding, the established practice catalog, and project practices created earlier in the run.
2. Require the resolver to reuse an applicable practice or establish one new project-scoped practice.
3. Require the resolver to return a complete, architecture-versioned ADR as part of its resolution.
4. Persist the resolution, selected practice, and ADR as a resolution artifact.
5. Run the existing constrained decision integrator for that single resolution.
6. Validate and persist the resulting architecture patch.
7. Pass the updated architecture and project-practice state to the next finding.

Built-in practices are exposed through `IArchitecturePracticeProvider`, allowing another provider to replace the catalog later. Newly produced practices remain project-scoped and run-scoped. They are not promoted automatically into the established catalog.

The existing `ArchitectureDecisionIntegrator` remains the patch-application model for now. Replacing it with a deterministic `CommitArchitectureDecision` operation, global practice memory, candidate-practice promotion, multi-model arbitration, and an additional practice review are explicitly deferred.

## Consequences

- A later resolver can reuse decisions established by an earlier resolver.
- The integration prompt handles one resolution rather than consolidating an entire wave.
- Parallel resolution latency is traded for consistency and smaller integration contexts.
- Finding order can influence later decisions; review order is currently authoritative and deterministic.
- Established practices and project decisions remain distinct.
- Architecture versions advance once per integrated finding.
- Independent architecture review continues after the sequential resolution loop.

## Alternatives considered

### Resolve all findings in parallel and consolidate afterward

Rejected for now because it permits conflicting conventions and requires a larger semantic consolidation step.

### Allow activities to mutate a global practice catalog immediately

Rejected because project-specific decisions could become universal policy without evidence or review.

### Wait for a complete memory and commit subsystem

Rejected because a workflow-carried practice state provides the consistency benefit now while preserving a migration path to durable memory later.
