# ADR-0004: Anchor planning in mission, use cases, and verifiable scenarios

- Status: Accepted
- Date: 2026-08-15

## Context

Architecture generation currently starts from a domain-discovery artifact, but the final architecture review has no compact, immutable definition of success. Repeated broad reviews can therefore discover progressively less important improvements, mistake unspecified optional behavior for omissions, and fail to converge.

Large final planning artifacts also make decomposition expensive. A model must rediscover which requirements, contracts, decisions, and tests belong to its target. This weakens bounded-context isolation and makes later repairs broader than necessary.

Guyabano already discovers domain capabilities and Given/When/Then acceptance criteria. These need stronger identity and traceability so they remain useful after architecture generation.

## Decision

Guyabano will use an intent-first planning chain:

1. Domain discovery establishes a concise product mission containing guiding intent, success outcomes, explicit constraints, and non-goals.
2. Domain discovery identifies capabilities and first-class use cases. Each use case declares its owning capability, actor, objective, inputs, preconditions, business rules, successful outcomes, error outcomes, and acceptance scenarios.
3. Acceptance scenarios are expressed as observable Given/When/Then behavior and receive deterministic IDs when the executable plan is assembled.
4. Solution topology assigns capabilities, and therefore their use cases and scenarios, to bounded contexts.
5. Contracts, components, modules, tasks, and acceptance criteria retain bounded-context ownership and stable references.
6. Downstream prompts receive the mission plus only the use cases, scenarios, contracts, decisions, and dependencies relevant to their bounded context.
7. Architecture review treats the mission, non-goals, use cases, and acceptance scenarios as immutable requirement anchors. A blocking finding must cite an anchor or a concrete contradiction that prevents implementation.
8. The first architecture review is broad. Later passes are convergence reviews: they verify the preceding findings against the amended plan, retain an existing finding ID only when it remains unresolved, and look only for blocking contradictions or regressions introduced by the amendments.
9. Mechanical checks such as missing package versions, invalid references, graph cycles, and absent test infrastructure should move to deterministic validators rather than consume semantic review passes.
10. Accepted artifacts preserve the traceability chain from mission to use case, acceptance scenario, architecture contract, implementation task, generated files, and executable verification.

Acceptance scenarios are immutable during ordinary implementation and repair. Changing one is a requirements amendment, not a way to make a failing test pass.

## Consequences

### Positive

- Review has a finite definition of done and is less likely to drift into speculative improvements.
- Bounded-context prompts can be constructed from smaller graph projections.
- Human readers can trace why architecture, code, and tests exist.
- Requirement changes can invalidate only affected downstream artifacts.
- Failed tasks can be regenerated or escalated without discarding unrelated domains.
- Acceptance scenarios can later become BDD-style executable tests while remaining independent of implementation details.

### Negative

- Planning introduces additional typed artifacts and validation rules.
- Stable identity and provenance must be maintained across architecture amendments.
- Poorly chosen use-case boundaries can still produce artificial decomposition.
- Not every quality attribute is expressible as an executable BDD scenario and other verification kinds remain necessary.

## Alternatives considered

### Continue reviewing the complete plan without requirement anchors

Rejected because an open-ended reviewer has no stable stopping condition and can continually introduce optional concerns.

### Treat capabilities as sufficient specifications

Rejected because a capability name does not capture actors, behavior, error outcomes, or concrete verification examples.

### Split primarily by technical layer

Rejected because UI, DI, persistence, and testing are different concern kinds rather than business boundaries. Guyabano partitions primarily by business capability and bounded context, then applies technical and verification concerns as overlays.

### Generate acceptance tests only after implementation

Rejected because implementation models could weaken expected behavior to match their code. Acceptance scenarios must be established before architecture and implementation.

## Follow-up

- Add graph projections for decomposition and implementation prompts.
- Persist generated-file provenance back to implementation tasks and scenarios.
- Add selective invalidation from changed mission constraints, use cases, or scenarios.
- Separate deterministic architecture linting from semantic review.
- Measure review-pass convergence and token use before and after targeted verification.
