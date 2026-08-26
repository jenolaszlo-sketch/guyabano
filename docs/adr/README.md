# Architecture decision records

Guyabano uses architecture decision records to preserve consequential design choices and their tradeoffs.

## Convention

- Use a four-digit sequence followed by a short kebab-case title.
- Record one decision per file.
- Use the statuses `Proposed`, `Accepted`, `Superseded`, or `Deprecated`.
- Do not rewrite an accepted decision to change history. Add another ADR and link the superseded decision.

## Decisions

| ADR | Status | Decision |
| --- | --- | --- |
| [ADR-0001](0001-replace-semantic-architecture-retries-with-map-reduce.md) | Accepted | Replace semantic architecture retries with a map/reduce loop |
| [ADR-0002](0002-integrate-resolved-architecture-findings-as-patches.md) | Accepted | Integrate resolved architecture findings as constrained patches |
| [ADR-0003](0003-resolve-architecture-findings-sequentially-with-practices.md) | Accepted | Resolve architecture findings sequentially with shared practices |
| [ADR-0004](0004-anchor-planning-in-mission-use-cases-and-verifiable-scenarios.md) | Accepted | Anchor planning in mission, use cases, and verifiable scenarios |
| [ADR-0005](0005-continue-workflows-from-artifact-checkpoints.md) | Accepted | Continue workflows from artifact checkpoints |
| [ADR-0006](0006-model-project-roles-and-component-relationships-explicitly.md) | Accepted | Model project roles and component relationships explicitly |
| [ADR-0007](0007-replace-temporal-and-redis-with-embedded-zhinu.md) | Accepted | Replace Temporal and Redis with embedded Zhinu |
