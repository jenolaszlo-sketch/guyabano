# ADR-0002: Integrate resolved architecture findings as constrained patches

- Status: Accepted
- Date: 2026-08-14

## Context

ADR-0001 separates architecture-gap discovery and resolution from integration. The original integration schema still asked one model to accept or reject each review finding. That made the integration activity another architecture review and allowed it to overturn focused decisions, widen the change, or spend tokens reasoning about issues that had already been resolved.

Guyabano also needs architecture changes to remain traceable. A resolved finding should lead to a specific patch and then to a new validated architecture version without silently replacing unrelated work.

## Decision

Use an `ArchitectureDecisionIntegrator` after focused resolution.

The integrator:

1. Treats every supplied focused resolution as authoritative.
2. Emits a minimal `ArchitectureDecisionPatch`, never a replacement plan.
3. Identifies every applied resolution exactly once.
4. May replace only existing entities named by the resolved findings' `affectedIds`.
5. Propagates decisions into the contracts, ADRs, architecture notes, acceptance criteria, and tasks required for internal coherence.
6. Does not accept, reject, reinterpret, or independently review a resolution.

Guyabano applies the patch deterministically and validates the resulting `CodeGenerationPlan`. A successful integration is stored as an `architecture-decision-patch` artifact linked to the previous architecture artifact and the focused resolution artifacts. A later accepted architecture artifact includes that patch in its input chain.

The Temporal activity wire name remains unchanged for workflow-history compatibility even though the code and UI use decision-integration terminology.

## Consequences

- Focused resolvers own decisions; the integrator owns representation and propagation.
- Architecture review remains an independent verification step after integration.
- The integration prompt is smaller and less discretionary.
- Unrelated architecture replacements fail deterministic validation.
- New additions are still possible when an authoritative resolution introduces a missing entity; their semantic necessity is checked by the subsequent architecture review.
- Patch artifacts make decision provenance and selective reruns easier to implement later.

## Alternatives considered

### Let the amendment model accept or reject findings

Rejected because it duplicates review and resolution, weakens artifact ownership, and can discard valid focused decisions.

### Rebuild the complete architecture after every finding

Rejected because it consumes more tokens, creates unnecessary drift, and invalidates unrelated downstream artifacts.

### Apply resolution prose without a structured patch

Rejected because deterministic validation, provenance, and selective invalidation require machine-readable changes.
