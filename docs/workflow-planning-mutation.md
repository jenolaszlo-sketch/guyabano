# Guyabano Workflow Planning and Mutation Specification

> Status (2026-09-18): design proposal, not yet implemented. It extends the
> vision in `ROADMAP.md` with a concrete planning/mutation architecture.
> Alignment notes and open risks are collected at the end of this file;
> they are review comments, not part of the proposal itself.

## 1. Purpose

Guyabano should plan software work by progressively producing durable planning artifacts inside a Zhinu workflow.

Planning must not happen as an external pre-processing step that eventually creates a separate execution workflow. Instead, the planning process itself is a workflow. As planning artifacts become sufficiently complete, the existing workflow is mutated into a richer workflow containing the implementation, integration, validation, and corrective work required to complete the goal.

This provides two immediate benefits:

1. Planning becomes observable, durable, resumable, and auditable.
2. The Guyabano integration becomes a practical test bed for Zhinu workflow mutation.

The core model is:

```text
Initial workflow
    ↓
Planning artifacts
    ↓
Execution design
    ↓
Fuwen workflow definition
    ↓
Zhinu workflow mutation
    ↓
Implementation workflow
    ↓
Further discoveries or artifact changes
    ↓
Selective regeneration
    ↓
Further workflow mutation
```

The workflow evolves as knowledge about the problem becomes more precise.

---

# 2. Design principles

## 2.1 Planning is execution

Planning is not a special stage outside the workflow system.

Activities such as:

* understanding the goal;
* defining architecture;
* defining contracts;
* decomposing work;
* defining dependencies;
* selecting execution capabilities;
* producing Fuwen;

are normal workflow activities.

They consume artifacts and produce new artifacts.

## 2.2 Artifacts are first-class

Planning output must not exist only inside model context.

Each important planning result becomes a durable artifact with identity, revision, provenance, and dependencies.

Examples include:

```text
requirements
architecture
contracts
work-units
execution-graph
bindings
workflow-definition
```

## 2.3 Decomposition must reach contract-complete boundaries

Work must not be split merely because components appear independent.

Before independent implementation tasks are created, their integration boundaries must be known.

At minimum, the planner should establish:

* provided interfaces;
* required interfaces;
* operations;
* shared types;
* important invariants;
* dependency direction;
* externally visible behavior.

The planner does not need to define every private implementation class.

The stopping criterion is therefore not "class diagram complete."

It is:

> Independently executable work must have sufficiently defined contracts to compose correctly.

## 2.4 Artifacts form a dependency graph

Artifacts are derived from other artifacts.

For example:

```text
Goal
 ↓
Requirements
 ↓
C4 Architecture
 ↓
Contracts
 ↓
Work Units
 ↓
Execution Graph
 ↓
Bindings
 ↓
Fuwen
```

A revision to an upstream artifact may invalidate downstream artifacts.

Guyabano should use those dependencies to determine the smallest affected planning region.

## 2.5 Workflow mutation is driven by artifact changes

Artifacts describe knowledge.

Fuwen describes execution.

Zhinu mutation should occur when changes in planning knowledge require changes to executable workflow topology or node definitions.

Artifact changes therefore trigger:

```text
artifact revision
    ↓
impact analysis
    ↓
artifact invalidation
    ↓
selective regeneration
    ↓
execution graph comparison
    ↓
Fuwen regeneration
    ↓
Zhinu workflow mutation
```

---

# 3. System responsibilities

## Guyabano

Guyabano owns planning semantics.

Responsibilities:

* goal interpretation;
* architecture decomposition;
* contract generation;
* work decomposition;
* dependency analysis;
* acceptance criteria;
* execution graph construction;
* capability selection;
* model/tool binding;
* artifact invalidation;
* impact analysis;
* Fuwen authoring requests;
* mutation decision making.

Guyabano decides what work should exist.

## Fuwen

Fuwen describes executable workflow structure.

Responsibilities include:

* nodes;
* inputs and outputs;
* dependencies;
* conditions;
* loops;
* fan-out;
* joins;
* signals;
* capability requirements;
* artifact references;
* execution semantics.

Fuwen should not need to understand C4, UML, software architecture, or why a contract was chosen.

Guyabano has already resolved those questions.

## Zhinu

Zhinu owns durable workflow execution.

Responsibilities include:

* workflow instances;
* persistence;
* retries;
* leases;
* scheduling;
* recovery;
* compensation;
* artifacts;
* workflow versions;
* workflow mutation;
* preservation of valid prior work;
* execution of newly introduced or invalidated nodes.

Zhinu does not decide architecture.

It executes the graph Guyabano has produced.

---

# 4. Planning artifact model

Planning artifacts should have stable identities and revisions.

Conceptually:

```text
Artifact
    Kind
    Identity
    Revision
    Content
    ContentHash
    Inputs
    ProducedBy
    Status
```

Example:

```yaml
kind: contracts
identity: classification
revision: 7
contentHash: 37ac...

producedBy:
  workflowNode: generate-classification-contracts

inputs:
  - architecture/classification@4

status: valid
```

Suggested states:

```text
candidate
valid
stale
invalid
superseded
approved
```

Do not couple the initial implementation too tightly to all of these states if Zhinu already has an appropriate artifact lifecycle.

The essential requirements are:

* stable identity;
* revision;
* dependency/provenance information;
* ability to identify the artifact used by a workflow node.

---

# 5. Planning artifact types

## 5.1 Goal / requirements artifact

Represents the interpreted user objective and important constraints.

Example:

```yaml
goal:
  Build classification support for incoming tickets.

constraints:
  - preserve existing public API
  - use existing persistence abstraction

acceptance:
  - tickets can be classified
  - classification is persisted
  - invalid classifications are rejected
```

This artifact should remain relatively close to user intent.

---

## 5.2 C4 architecture artifact

Represents structural responsibility and system boundaries.

Recommended levels:

```text
Context
Container
Component
```

Class-level detail is not required here.

A suitable DSL may be used, preferably an existing compact C4 representation such as Structurizr DSL.

Example conceptually:

```text
SupportSystem
    SupportApi
        TicketClassification
        TicketRepository
```

The architecture artifact answers:

> What exists, and where does responsibility live?

A rendered C4 or PlantUML view may be generated from this artifact.

The diagram representation is a projection, not necessarily the authoritative planning state.

---

## 5.3 Contract artifact

This is the main boundary enabling safe decomposition.

Example:

```yaml
component: TicketClassification

provides:
  - TicketClassifier

requires:
  - TicketRepository
  - ModelClient

contracts:
  TicketClassifier:
    operations:
      classify:
        input: Ticket
        output: ClassificationResult

types:
  ClassificationResult:
    fields:
      category: Category
      confidence: float

invariants:
  - confidence >= 0
  - confidence <= 1
```

This artifact answers:

> What must independently implemented parts agree on?

Contract artifacts should be established before work is delegated into independent implementation branches.

---

## 5.4 Design projection

Guyabano may render contract artifacts into PlantUML.

Example:

```plantuml
interface TicketClassifier
interface TicketRepository
interface ModelClient

class ClassificationService

ClassificationService ..|> TicketClassifier
ClassificationService --> TicketRepository
ClassificationService --> ModelClient
```

This is useful for:

* human inspection;
* model review;
* architecture discussion;
* compact visualization.

PlantUML should initially be treated as a projection of planning state rather than an additional source of truth.

If later Guyabano supports editing diagrams directly, changes should be parsed back into the corresponding authoritative artifact rather than allowing two independent models to drift.

---

## 5.5 Work-unit artifact

Represents independently executable work.

Example:

```yaml
id: implement-classification-service

owns:
  - ClassificationService

implements:
  - TicketClassifier

uses:
  - TicketRepository
  - ModelClient

inputs:
  - contracts/TicketClassifier
  - types/Ticket
  - types/ClassificationResult

acceptance:
  - implements TicketClassifier
  - classification result satisfies contract invariants
  - project builds
  - relevant tests pass
```

A component may produce several work units.

A work unit should be small enough to execute with bounded context.

---

# 6. Work decomposition stopping criteria

Guyabano should continue decomposition until a work unit satisfies both structural and execution criteria.

## Structural readiness

A unit should not be delegated until:

* external contracts are known;
* required shared types are known;
* dependencies are known;
* ownership is reasonably clear;
* externally observable behavior is defined.

## Execution readiness

A unit should be small enough that:

* required context can be bounded;
* acceptance criteria are testable;
* failure can be detected;
* retries are meaningful;
* the unit does not contain several independently retryable concerns;
* further decomposition would not materially improve isolation, parallelism, or model selection.

This distinction is important.

Architecture decomposition and execution decomposition may stop at different depths.

---

# 7. Execution graph artifact

Once work units are defined, Guyabano creates a semantic execution graph.

Example:

```text
Create Contracts
      ↓
 ┌────┴─────┐
 ↓          ↓
Repo       Classifier
 ↓          ↓
 └────┬─────┘
      ↓
 Integration
      ↓
     Tests
```

The graph should describe:

* dependencies;
* parallelizable work;
* joins;
* validation;
* corrective branches;
* checkpoints;
* required artifacts;
* produced artifacts.

This artifact remains semantic.

It should not yet depend heavily on provider-specific execution details.

---

# 8. Binding artifact

Execution nodes are then bound to capabilities.

Example:

```yaml
implement-classifier:
  capability: code.modify
  modelProfile: implementation
  context:
    - contracts/TicketClassifier
    - source/Classification

review-architecture:
  capability: architecture.reason
  modelProfile: frontier

run-tests:
  capability: process.execute
```

The binding artifact answers:

> How should each execution node be performed?

Model selection should be represented through profiles or capabilities rather than hardcoding a specific provider or model into architectural artifacts.

For example:

```text
architecture.reason
implementation
mechanical
verification
```

Baize or the execution environment can resolve those profiles to concrete providers/models.

---

# 9. Fuwen generation

Once the execution graph and bindings are sufficiently resolved, Guyabano generates Fuwen.

Fuwen generation should be treated as compilation from planning artifacts rather than asking the author model to rediscover the plan.

The Fuwen author should receive primarily:

* relevant execution graph;
* node bindings;
* required artifact identities;
* relevant capability descriptors;
* Fuwen grammar;
* required execution semantics.

The author should not need the full planning history unless required for a specific node.

Conceptually:

```text
Planning Artifacts
       ↓
Semantic Execution Graph
       ↓
Capability Binding
       ↓
Fuwen Author
       ↓
Fuwen Compiler
       ↓
Zhinu Workflow Definition
```

---

# 10. Bootstrap workflow

The first workflow does not need to be generated by Fuwen.

Guyabano can create a small planning workflow using the normal Zhinu API.

Initial workflow:

```text
InterpretGoal
    ↓
PlanArchitecture
    ↓
PlanContracts
    ↓
DecomposeWork
    ↓
BuildExecutionGraph
    ↓
ResolveBindings
    ↓
GenerateFuwen
    ↓
CompileFuwen
    ↓
ApplyWorkflowMutation
```

This avoids a bootstrap circular dependency.

Later, this standard planning workflow may itself be represented in Fuwen if useful.

That is not required for the initial implementation.

---

# 11. Workflow mutation model

After Fuwen has been generated and compiled, Guyabano should use Zhinu workflow mutation to evolve the current workflow.

Example initial workflow:

```text
Workflow v1

Interpret Goal       complete
Plan Architecture    complete
Plan Contracts       complete
Decompose Work       complete
Build Graph          complete
Generate Fuwen       complete
```

Mutation produces:

```text
Workflow v2

Interpret Goal       preserved
Plan Architecture    preserved
Plan Contracts       preserved
Decompose Work       preserved
Build Graph          preserved
Generate Fuwen       preserved

                     Implement A
                    /
                   /
                  +---- Implement B
                   \
                    \
                     Implement C
                          ↓
                       Integrate
                          ↓
                         Test
```

Completed planning work and its artifacts remain part of the workflow lineage.

Only newly introduced executable work should run.

---

# 12. Preservation rules

Workflow mutation must preserve completed work whenever its inputs remain valid.

Example:

```text
A completed against Contracts@7
```

If:

```text
Contracts@7
```

is still valid after mutation, the work should remain reusable.

If:

```text
Contracts@7 → Contracts@8
```

changes an interface used by A, then A may become invalid and must be rerun.

The goal is not to restart the workflow.

The goal is to rerun only the affected subgraph.

---

# 13. Artifact invalidation

When an artifact changes, Guyabano should determine its downstream effects.

Example:

```text
Architecture.Classification@4
          ↓
Contracts.Classification@7
          ↓
WorkUnit.Classification@3
          ↓
ExecutionGraph.Classification@2
          ↓
Bindings.Classification@1
          ↓
Fuwen.Classification@1
```

If architecture changes to:

```text
Architecture.Classification@5
```

Guyabano marks affected derived artifacts stale.

Unaffected branches remain valid.

For example:

```text
Authentication
Billing
CustomerProfile
```

should not be regenerated merely because Classification changed.

---

# 14. Mutation triggered during execution

Execution itself may discover that planning assumptions were incorrect.

Example:

```text
Implement Classification
        ↓
Agent discovers contract is insufficient
        ↓
ContractRevisionRequested
        ↓
Contracts@8
        ↓
Impact Analysis
        ↓
affected work units regenerated
        ↓
new execution graph
        ↓
Fuwen@2
        ↓
Workflow v3
```

This is a critical use case.

Workflow mutation should therefore not be limited to the initial transition from planning to implementation.

It is part of normal adaptive execution.

---

# 15. Mutation workflow

The mutation process should itself be represented as workflow activity.

Conceptually:

```text
ArtifactChanged
      ↓
DetermineImpact
      ↓
MarkAffectedArtifactsStale
      ↓
RegenerateAffectedArtifacts
      ↓
ValidateArtifacts
      ↓
RebuildAffectedExecutionGraph
      ↓
ResolveAffectedBindings
      ↓
RegenerateAffectedFuwen
      ↓
Compile
      ↓
ApplyMutation
```

This keeps regeneration explicit and durable.

---

# 16. Avoid self-modifying workflow ambiguity

The currently executing workflow should not directly rewrite itself in an uncontrolled way.

Use versioned mutation:

```text
Workflow v1
    ↓
candidate Workflow v2
    ↓
validate
    ↓
apply mutation
    ↓
Workflow v2 becomes current
```

The old version remains available for history and recovery according to existing Zhinu mutation semantics.

The model proposes planning artifacts and Fuwen.

Zhinu controls the actual version transition.

---

# 17. Three related graphs

Guyabano should conceptually distinguish three graphs.

## Design graph

Represents planning knowledge.

```text
Requirements
 ↓
Architecture
 ↓
Contracts
 ↓
Work Units
 ↓
Execution Design
```

## Execution graph

Represents executable work.

```text
Implement A ─┐
             ├→ Integrate → Test
Implement B ─┘
```

## Provenance graph

Represents why artifacts exist.

```text
Contract@7
  produced by GenerateContracts
  from Architecture@4
```

These graphs may share identities and relationships but should not be conflated.

---

# 18. Initial implementation scope

The first implementation should intentionally remain small.

Use a constrained planning pipeline:

```text
Goal
 ↓
Architecture
 ↓
Contracts
 ↓
Work Units
 ↓
Execution Graph
 ↓
Bindings
 ↓
Fuwen
 ↓
Mutation
```

Do not initially attempt:

* automatic multi-level C4 refinement across large repositories;
* sophisticated semantic diffing of arbitrary architecture artifacts;
* automatic human approval workflows;
* complex compensation across planning versions;
* fully automatic diagram round-tripping;
* advanced model routing optimization.

This objective is to prove the planning and mutation architecture.

---

# 19. Suggested first test scenario

Use a deliberately small feature with at least two parallel implementation branches.

Example:

> Add ticket classification with persistence.

Expected planning output:

```text
Architecture
    Ticket Classification
    Ticket Repository

Contracts
    ITicketClassifier
    ITicketRepository
    Ticket
    ClassificationResult

Work Units
    Implement Classification
    Implement Repository Changes
    Integration

Execution Graph

             ┌→ Implement Classifier ─┐
Contracts ───┤                        ├→ Integrate → Test
             └→ Implement Repository ─┘
```

Initial planning workflow produces these artifacts and mutates into the implementation workflow.

---

# 20. Required workflow mutation tests

The Guyabano integration should explicitly test Zhinu mutation.

## Test 1: Planning to execution mutation

Start with planning-only workflow.

Generate planning artifacts and Fuwen.

Mutate into implementation workflow.

Verify:

* planning nodes remain completed;
* planning artifacts remain available;
* new execution nodes are introduced;
* only new nodes execute;
* workflow lineage records both versions.

## Test 2: Add work after execution starts

During implementation, introduce a newly discovered work unit.

Example:

```text
Classifier requires cache support.
```

Produce:

```text
IClassificationCache
ImplementClassificationCache
```

Mutate the workflow.

Verify:

* completed unaffected branches remain complete;
* new branch executes;
* dependent integration/test nodes are invalidated or replaced as appropriate;
* previous artifacts remain available.

## Test 3: Contract change

Change:

```text
TicketClassifier
```

from:

```text
classify(Ticket) -> ClassificationResult
```

to a materially different contract.

Verify:

* dependent work units become stale;
* unrelated work remains valid;
* affected Fuwen nodes are regenerated;
* affected execution nodes rerun;
* unaffected artifacts remain preserved.

## Test 4: Architecture change

Add a new architectural component after initial planning.

Example:

```text
ClassificationCache
```

Verify cascading regeneration:

```text
Architecture
 ↓
Contracts
 ↓
Work Units
 ↓
Execution Graph
 ↓
Bindings
 ↓
Fuwen
 ↓
Workflow mutation
```

Verify unaffected branches are retained.

## Test 5: Failed candidate mutation

Generate invalid Fuwen or a candidate graph that fails validation.

Verify:

* current workflow remains unchanged;
* candidate artifacts remain available for diagnostics;
* mutation is not partially applied;
* repair can produce a new candidate.

## Test 6: Restart recovery

Restart the process while a planning or mutation workflow is active.

Verify:

* planning artifacts are recovered;
* artifact revisions remain stable;
* the current workflow version is recovered;
* candidate mutation state is not confused with active state;
* execution resumes correctly.

---

# 21. Validation

Each stage should validate its output before downstream work proceeds.

Examples:

## Architecture validation

* all referenced components exist;
* relationships reference valid identities;
* component identities are stable.

## Contract validation

* required contracts exist;
* referenced types exist;
* required contracts have providers;
* operation identities are stable;
* basic dependency cycles are detected where inappropriate.

## Work-unit validation

* every work unit has acceptance criteria;
* dependencies reference known contracts/artifacts;
* ownership does not conflict unless explicitly allowed.

## Execution graph validation

* dependencies exist;
* graph is executable;
* required joins are valid;
* no impossible dependency cycles exist.

## Binding validation

* required capabilities can be resolved;
* required context artifacts exist.

## Fuwen validation

* grammar valid;
* identities valid;
* capability references valid;
* inputs and outputs match;
* compilation succeeds.

---

# 22. Model responsibilities

Models should have narrow responsibilities.

## Planning model

Responsible for:

* architectural decomposition;
* contract design;
* work decomposition;
* dependency reasoning;
* acceptance criteria.

It should not unnecessarily emit final Fuwen while architecture is still unresolved.

## Fuwen author model

Responsible for:

* translating an already-resolved execution plan into Fuwen.

It should not reinvent architecture.

## Implementation model

Responsible for:

* executing a bounded work unit against predefined contracts.

It should not freely redesign shared contracts unless it explicitly raises a planning issue.

## Review model or validator

May be used to verify:

* architecture;
* contracts;
* work decomposition;
* implementation;
* Fuwen.

Different model profiles may later be selected for each role.

---

# 23. Suggested repository abstractions

Names may be adjusted to existing Guyabano conventions.

Conceptually:

```text
PlanningArtifact
PlanningArtifactRevision

ArchitectureArtifact
ContractArtifact
WorkUnitArtifact
ExecutionGraphArtifact
BindingArtifact
FuwenArtifact
```

Supporting services may include:

```text
IPlanningArtifactStore
IPlanningArtifactDependencyGraph
IPlanningArtifactValidator
IPlanningImpactAnalyzer
IArchitecturePlanner
IContractPlanner
IWorkDecomposer
IExecutionGraphBuilder
IBindingResolver
IFuwenAuthor
IWorkflowMutationPlanner
```

Do not create unnecessary abstractions if equivalent concepts already exist in Zhinu, Hongxian, or Guyabano.

Prefer adapting existing primitives.

---

# 24. Important identity rule

Artifact identities must remain stable across revisions.

For example:

```text
contracts/TicketClassifier@1
contracts/TicketClassifier@2
contracts/TicketClassifier@3
```

should represent revisions of the same logical contract.

Do not generate unrelated random identities each time the model replans.

Stable identities are necessary for:

* dependency tracking;
* mutation;
* invalidation;
* artifact reuse;
* debugging;
* provenance.

---

# 25. Prompting strategy

Do not give every planner every artifact.

Each planning node should receive only the context needed for that stage.

Example:

```text
PlanContracts receives:
    relevant architecture
    relevant requirements
    existing contract constraints
```

It should not automatically receive unrelated implementation history.

Similarly:

```text
FuwenAuthor receives:
    execution graph
    bindings
    selected catalogue descriptors
    Fuwen grammar
    required execution semantics
```

This supports Guyabano's overall context-management strategy and reduces repeated token cost.

---

# 26. Expected lifecycle

A typical successful run should look like:

```text
1. User supplies goal.

2. Guyabano creates Workflow v1.

3. Workflow v1 produces:
   Requirements@1
   Architecture@1
   Contracts@1
   WorkUnits@1
   ExecutionGraph@1
   Bindings@1
   Fuwen@1

4. Fuwen@1 compiles successfully.

5. Guyabano requests mutation.

6. Zhinu creates Workflow v2.

7. Workflow v2 preserves planning history and adds implementation work.

8. Implementation branches execute.

9. Integration and verification execute.

10. If assumptions hold, workflow completes.

11. If execution discovers a planning problem:
    artifact revision is created
    affected descendants are regenerated
    Fuwen is updated
    Zhinu produces Workflow v3
    valid previous work is preserved.
```

---

# 27. Definition of done for first milestone

The first milestone is complete when Guyabano can:

* create a planning workflow in Zhinu;
* produce durable architecture, contract, work-unit, execution-graph, binding, and Fuwen artifacts;
* compile generated Fuwen;
* mutate the planning workflow into an implementation workflow;
* preserve completed planning artifacts across mutation;
* execute newly introduced work;
* revise one planning artifact;
* determine the affected downstream planning artifacts;
* generate a revised Fuwen workflow;
* apply a second mutation;
* preserve unaffected completed work;
* recover the workflow correctly after restart.

This milestone does not require sophisticated automatic architecture generation.

Hardcoded or simple model-generated planning artifacts are acceptable initially if they allow the full mutation lifecycle to be validated.

The important deliverable is the architecture of the planning and mutation mechanism.

---

# 28. Core architectural invariant

The implementation should preserve this rule:

> Planning knowledge lives in revisioned artifacts. Executable intent lives in Fuwen. Durable execution and workflow evolution live in Zhinu.

Guyabano connects the three.

A workflow begins with incomplete knowledge.

Planning activities progressively make that knowledge explicit.

Once enough knowledge exists, Guyabano compiles it into executable workflow structure.

When knowledge changes, Guyabano invalidates only the affected derived artifacts and Zhinu evolves the workflow while preserving valid prior work.

This is the intended foundation for adaptive Guyabano workflows.

---

# Appendix A. Alignment review (2026-09-18)

The proposal matches the established architecture: Guyabano/Fuwen/Zhinu
responsibilities (§3) mirror the current system boundaries; the bootstrap
workflow (§10) is today's hard-coded `CodeGenerationWorkflow`; model roles
(§22) map to existing planner/reviewer profiles; the identity rule (§24)
matches digest-pinned descriptors; §9's author-input list matches the
direction of the `workflow-authoring` pack (which should evolve to take
execution graphs, not just catalogues).

## Open risks and gaps

1. **Zhinu mutation is unproven (biggest risk).** §11–16 and tests 1–6
   presuppose versioned workflow mutation with lineage preservation.
   Proven today: step restart, durable replay, wait/signal. Not proven:
   v1→v2 topology evolution preserving completed work. This needs a spike
   against Zhinu's actual API before committing to the milestone tests.
2. **Fuwen author input.** §9 wants execution graph + bindings +
   descriptors; our author pack currently takes catalogue + request only.
   Evolve the pack as execution-graph artifacts come online.
3. **Closed regions.** Generated Fuwen must respect closed-region rules
   (bodies cannot read outer outputs); the author pack already teaches
   this, and it constrains what execution graphs can express.
4. **Compile–repair loop.** §20 test 5 (failed candidate) overlaps our
   existing author repair loop; keep both, they test different layers
   (candidate planning vs. candidate syntax).
5. **Approval gates.** §18 defers human approval, but Fuwen `wait` nodes
   already provide the mechanism; revisit when the milestone needs it.

Suggested phasing: Phase 0 = Zhinu mutation spike; Phase 1 = artifact
store + bootstrap (largely exists); Phase 2 = author-from-artifacts;
Phase 3 = mutation tests 1–6.
