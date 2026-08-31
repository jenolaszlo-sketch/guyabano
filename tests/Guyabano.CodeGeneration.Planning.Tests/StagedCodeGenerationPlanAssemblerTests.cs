using FluentAssertions;

namespace Guyabano.CodeGeneration.Planning.Tests;

public sealed class StagedCodeGenerationPlanAssemblerTests
{
    [Fact]
    public void Assemble_CreatesDeterministicExecutablePlan()
    {
        var artifacts = CreateArtifacts();

        var first = StagedCodeGenerationPlanAssembler.Assemble(artifacts);
        var second = StagedCodeGenerationPlanAssembler.Assemble(artifacts);

        first.Tasks.Should().HaveCount(2);
        first.Tasks[0].ExecutionKind.Should().Be(
            PlanTaskExecutionKind.Scaffolding);
        first.Tasks[1].DependsOn.Should().Contain(first.Tasks[0].Id);
        first.Tasks[1].ContractIds.Should().ContainSingle();
        first.Tasks[1].Relationships.DefinesContractIds
            .Should().ContainSingle();
        first.Tasks[1].Relationships.ImplementsPortContractIds
            .Should().ContainSingle();
        first.Tasks[1].AcceptanceCriterionIds.Should().ContainSingle();
        first.Tasks.Select(item => item.Id).Should().Equal(
            second.Tasks.Select(item => item.Id));
        first.ArchitectureNotes.Should().HaveCount(2);
        first.Mission.GuidingIntent.Should().Be("Track todos.");
        first.UseCases.Should().ContainSingle().Which.BoundedContext
            .Should().Be("Todos");
        first.AcceptanceCriteria.Should().ContainSingle().Which.UseCaseId
            .Should().Be(first.UseCases[0].Id);
        first.Modules.Should().ContainSingle().Which.BoundedContext
            .Should().Be("Todos");
        first.ArchitectureNotes.Should().Contain(item =>
            item.Category == ArchitectureNoteCategory.InferredDomainConstraint);
        first.Decisions.Should().ContainSingle()
            .Which.Title.Should().Be("Service contract boundary");
    }

    [Fact]
    public void ValidateDomain_RejectsCapabilityWithoutUseCase()
    {
        var artifacts = CreateArtifacts();
        artifacts.Domain.UseCases.Clear();

        StagedPlanningValidator.ValidateDomain(artifacts.Domain)
            .Should().Contain(error =>
                error.Contains("no use cases", StringComparison.Ordinal) ||
                error.Contains("has no use case", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DuplicateModelNamesBecomeCorrectionFeedbackInsteadOfExceptions()
    {
        var artifacts = CreateArtifacts();
        artifacts.Topology.Projects.Add(artifacts.Topology.Projects[0]);
        artifacts.Topology.BoundedContexts.Add(
            artifacts.Topology.BoundedContexts[0]);
        artifacts.Topology.Modules.Add(artifacts.Topology.Modules[0]);
        artifacts.ContractCatalogs[0].Contracts.Add(
            artifacts.ContractCatalogs[0].Contracts[0]);
        artifacts.ComponentManifests[0].Components.Add(
            artifacts.ComponentManifests[0].Components[0]);

        var errors = StagedPlanningValidator.Validate(artifacts);

        errors.Should().Contain(error => error.Contains(
            "Duplicate project name 'TodoApi'", StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains(
            "Duplicate bounded context name 'Todos'", StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains(
            "Duplicate topology module name 'TodoApi.Api'", StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains(
            "Duplicate contract name 'ITodoService'", StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains(
            "Duplicate component name 'TodoService'", StringComparison.Ordinal));
    }

    [Fact]
    public void Assemble_DoesNotInferAcceptanceOwnershipFromCapability()
    {
        var artifacts = CreateArtifacts();
        artifacts.ComponentManifests[0].Components.Add(
            new StagedComponent
            {
                Name = "TodoDto",
                Kind = "Record",
                ModuleName = "TodoApi.Api",
                ProjectName = "TodoApi",
                Files = ["src/TodoApi/TodoDto.cs"],
                Responsibilities = ["Carry todo data."],
                DefinesContractNames = [],
                ImplementsPortNames = [],
                ConsumesContractNames = [],
                UsesConcreteComponentNames = [],
                RegistersImplementationNames = [],
                TestsComponentNames = [],
                CapabilityNames = ["CreateTodo"],
                AcceptanceCriterionIds = [],
                Lifetime = "None",
                ComplexityPoints = 1,
                VerificationKinds = ["Compilation"]
            });

        var plan = StagedCodeGenerationPlanAssembler.Assemble(artifacts);

        plan.Tasks.Single(task => task.Title.Contains("TodoDto"))
            .AcceptanceCriterionIds.Should().BeEmpty();
        plan.Tasks.Single(task => task.Title.Contains("TodoService"))
            .AcceptanceCriterionIds.Should().ContainSingle();
    }

    [Fact]
    public void ValidateComponents_RejectsUnownedAcceptanceCriterion()
    {
        var artifacts = CreateArtifacts();
        artifacts.ComponentManifests[0].Components[0]
            .AcceptanceCriterionIds.Clear();

        StagedPlanningValidator.ValidateComponents(
                artifacts.Domain,
                artifacts.Topology,
                artifacts.ContractCatalogs,
                artifacts.ComponentManifests)
            .Should().Contain(error =>
                error.Contains("has no owning component"));
    }

    [Fact]
    public void ValidateComponents_ReportsUnknownContractAndUnsupportedPoints()
    {
        var artifacts = CreateArtifacts();
        var validComponent = artifacts.ComponentManifests[0].Components[0];
        var invalidComponent = new StagedComponent
        {
            Name = validComponent.Name,
            Kind = validComponent.Kind,
            ModuleName = validComponent.ModuleName,
            ProjectName = validComponent.ProjectName,
            Files = validComponent.Files,
            Responsibilities = validComponent.Responsibilities,
            DefinesContractNames = validComponent.DefinesContractNames,
            ImplementsPortNames = validComponent.ImplementsPortNames,
            ConsumesContractNames = ["MissingContract"],
            UsesConcreteComponentNames = validComponent.UsesConcreteComponentNames,
            RegistersImplementationNames = validComponent.RegistersImplementationNames,
            TestsComponentNames = validComponent.TestsComponentNames,
            CapabilityNames = validComponent.CapabilityNames,
            AcceptanceCriterionIds = validComponent.AcceptanceCriterionIds,
            Lifetime = validComponent.Lifetime,
            ComplexityPoints = 8,
            VerificationKinds = validComponent.VerificationKinds
        };
        var invalidManifest = new BoundedContextComponentManifest
        {
            BoundedContextName = "Todos",
            Components = [invalidComponent],
            Decisions = [],
            InferredDefaults = []
        };

        var errors = StagedPlanningValidator.ValidateComponents(
            artifacts.Domain,
            artifacts.Topology,
            artifacts.ContractCatalogs,
            [invalidManifest]);

        errors.Should().Contain(item => item.Contains("MissingContract"));
        errors.Should().Contain(item => item.Contains("unsupported complexity"));
    }

    [Fact]
    public void ValidateComponents_ReportsExplicitDependencyCyclePath()
    {
        var artifacts = CreateArtifacts();
        var service = artifacts.ComponentManifests[0].Components[0];
        service.UsesConcreteComponentNames.Add("TodoRepository");
        artifacts.ComponentManifests[0].Components.Add(new StagedComponent
        {
            Name = "TodoRepository",
            Kind = "Adapter",
            ModuleName = "TodoApi.Api",
            ProjectName = "TodoApi",
            Files = ["src/TodoApi/TodoRepository.cs"],
            Responsibilities = ["Store todos."],
            DefinesContractNames = [],
            ImplementsPortNames = [],
            ConsumesContractNames = [],
            UsesConcreteComponentNames = ["TodoService"],
            RegistersImplementationNames = [],
            TestsComponentNames = [],
            CapabilityNames = ["CreateTodo"],
            AcceptanceCriterionIds = [],
            Lifetime = "Singleton",
            ComplexityPoints = 1,
            VerificationKinds = ["Compilation"]
        });

        var errors = StagedPlanningValidator.ValidateComponents(
            artifacts.Domain,
            artifacts.Topology,
            artifacts.ContractCatalogs,
            artifacts.ComponentManifests);

        errors.Should().Contain(error =>
            error.Contains("TodoRepository -> TodoService -> TodoRepository"));
    }

    [Fact]
    public void ValidateComponents_RejectsTestRelationshipOutsideTestProject()
    {
        var artifacts = CreateArtifacts();
        var component = artifacts.ComponentManifests[0].Components[0];
        component.TestsComponentNames.Add(component.Name);

        StagedPlanningValidator.ValidateComponents(
                artifacts.Domain,
                artifacts.Topology,
                artifacts.ContractCatalogs,
                artifacts.ComponentManifests)
            .Should().Contain(error => error.Contains(
                "tests components outside a Test project"));
    }

    [Fact]
    public void ValidateComponents_RequiresExactlyOneContractDefiner()
    {
        var artifacts = CreateArtifacts();
        artifacts.ComponentManifests[0].Components[0]
            .DefinesContractNames.Clear();

        StagedPlanningValidator.ValidateComponents(
                artifacts.Domain,
                artifacts.Topology,
                artifacts.ContractCatalogs,
                artifacts.ComponentManifests)
            .Should().Contain("Contract 'ITodoService' has no defining component.");
    }

    [Fact]
    public void Assemble_ReconcilesContractProjectReferencesWithoutDependingOnConcreteImplementer()
    {
        var artifacts = CreateArtifacts();
        artifacts.Topology.Projects.Add(new PlannedProject
        {
            Name = "TodoCore",
            Path = "src/TodoCore/TodoCore.csproj",
            Kind = "Library",
            Role = ProjectRole.Domain,
            TargetFramework = "net10.0",
            Responsibilities = ["Own inward ports."],
            ProjectDependencies = [],
            Packages = []
        });
        artifacts.Topology.Projects.Add(new PlannedProject
        {
            Name = "TodoStorage",
            Path = "src/TodoStorage/TodoStorage.csproj",
            Kind = "Library",
            Role = ProjectRole.Adapter,
            TargetFramework = "net10.0",
            Responsibilities = ["Implement storage ports."],
            ProjectDependencies = [],
            Packages = []
        });
        artifacts.Topology.Modules.Add(new TopologyModulePlan
        {
            Name = "TodoCore.Ports",
            BoundedContextName = "Todos",
            ProjectName = "TodoCore",
            Responsibilities = ["Define storage ports."]
        });
        artifacts.Topology.Modules.Add(new TopologyModulePlan
        {
            Name = "TodoStorage.Adapter",
            BoundedContextName = "Todos",
            ProjectName = "TodoStorage",
            Responsibilities = ["Implement storage ports."]
        });
        artifacts.ContractCatalogs[0].Contracts.Add(new StagedContract
        {
            Name = "ITodoStore",
            Kind = "Interface",
            ModuleName = "TodoCore.Ports",
            Purpose = "Persist todos.",
            Members = ["void Add(string title)"],
            CapabilityNames = ["CreateTodo"]
        });
        var service = artifacts.ComponentManifests[0].Components[0];
        service.ConsumesContractNames.Add("ITodoStore");
        artifacts.ComponentManifests[0].Components.Add(new StagedComponent
        {
            Name = "TodoStorePort",
            Kind = "Interface",
            ModuleName = "TodoCore.Ports",
            ProjectName = "TodoCore",
            Files = ["src/TodoCore/ITodoStore.cs"],
            Responsibilities = ["Define the storage port."],
            DefinesContractNames = ["ITodoStore"],
            ImplementsPortNames = [],
            ConsumesContractNames = [],
            UsesConcreteComponentNames = [],
            RegistersImplementationNames = [],
            TestsComponentNames = [],
            CapabilityNames = ["CreateTodo"],
            AcceptanceCriterionIds = [],
            Lifetime = "None",
            ComplexityPoints = 1,
            VerificationKinds = ["Compilation"]
        });
        artifacts.ComponentManifests[0].Components.Add(new StagedComponent
        {
            Name = "InMemoryTodoStore",
            Kind = "Adapter",
            ModuleName = "TodoStorage.Adapter",
            ProjectName = "TodoStorage",
            Files = ["src/TodoStorage/InMemoryTodoStore.cs"],
            Responsibilities = ["Store todos in memory."],
            DefinesContractNames = [],
            ImplementsPortNames = ["ITodoStore"],
            ConsumesContractNames = [],
            UsesConcreteComponentNames = [],
            RegistersImplementationNames = [],
            TestsComponentNames = [],
            CapabilityNames = ["CreateTodo"],
            AcceptanceCriterionIds = [],
            Lifetime = "Singleton",
            ComplexityPoints = 1,
            VerificationKinds = ["Compilation"]
        });

        var plan = StagedCodeGenerationPlanAssembler.Assemble(artifacts);

        plan.Projects.Single(project => project.Name == "TodoApi")
            .ProjectDependencies.Should().Contain("TodoCore")
            .And.NotContain("TodoStorage");
        plan.Projects.Single(project => project.Name == "TodoStorage")
            .ProjectDependencies.Should().Contain("TodoCore");
        var storeTask = plan.Tasks.Single(task =>
            task.Title.Contains("InMemoryTodoStore"));
        plan.Tasks.Single(task => task.Title.Contains("TodoService"))
            .DependsOn.Should().NotContain(storeTask.Id);
    }

    [Theory]
    [InlineData("FrameworkConvention", ArchitectureNoteCategory.PlatformConvention)]
    [InlineData("TechnicalConstraint", ArchitectureNoteCategory.TechnicalChoice)]
    [InlineData("LocalDefault", ArchitectureNoteCategory.InferredDefault)]
    public void NormalizeCategory_AcceptsSemanticModelAliases(
        string value,
        ArchitectureNoteCategory expected)
    {
        StagedCodeGenerationPlanAssembler.NormalizeCategory(value)
            .Should().Be(expected);
    }

    private static StagedPlanningArtifacts CreateArtifacts()
    {
        var domain = new DomainDiscovery
        {
            Mission = new ProductMission
            {
                GuidingIntent = "Track todos.",
                SuccessOutcomes = ["Todos can be created."],
                Constraints = [],
                NonGoals = ["Durable persistence"]
            },
            Title = "Todo API",
            Summary = "Tracks todos.",
            Terms = [],
            QualityAttributes = ["Testable"],
            Assumptions = [],
            ProductAmbiguities = [],
            Capabilities =
            [
                new DomainCapability
                {
                    Name = "CreateTodo",
                    Description = "Creates a todo.",
                    BusinessRules = ["A title is required."]
                }
            ],
            UseCases =
            [
                new DiscoveredUseCase
                {
                    Name = "CreateTodo",
                    CapabilityName = "CreateTodo",
                    Actor = "API consumer",
                    Objective = "Create a todo.",
                    Preconditions = [],
                    Inputs = ["title"],
                    BusinessRules = ["A title is required."],
                    Outcomes = ["A todo is created."],
                    ErrorOutcomes = ["Invalid titles are rejected."],
                    AcceptanceCriteria =
                    [
                        new DiscoveredAcceptanceCriterion
                        {
                            Scenario = "Create a valid todo",
                            Given = ["A valid title"],
                            When = ["The todo is submitted"],
                            Then = ["The todo is returned with an id"],
                            VerificationKinds = ["UnitTest"]
                        }
                    ]
                }
            ],
            InferredDefaults =
            [
                new DiscoveredDomainDefault
                {
                    Kind = "DomainConstraint",
                    Subject = "Title length",
                    MissingInformation = "No maximum was supplied.",
                    Decision = "Limit titles to 200 characters.",
                    Reasons = ["Titles remain useful."],
                    Impact = "Longer values are rejected.",
                    AffectedCapabilities = ["CreateTodo"],
                    UserOverridable = true
                }
            ]
        };
        var topology = new SolutionTopology
        {
            Solution = new PlannedSolution
            {
                Name = "TodoApi",
                Path = "TodoApi.sln"
            },
            Projects =
            [
                new PlannedProject
                {
                    Name = "TodoApi",
                    Path = "src/TodoApi/TodoApi.csproj",
                    Kind = "WebApi",
                    Role = ProjectRole.CompositionRoot,
                    TargetFramework = "net10.0",
                    Responsibilities = ["Host the API."],
                    ProjectDependencies = [],
                    Packages = []
                }
            ],
            BoundedContexts =
            [
                new BoundedContextPlan
                {
                    Name = "Todos",
                    Purpose = "Manage todos.",
                    CapabilityNames = ["CreateTodo"],
                    DependsOnContextNames = [],
                    InboundAdapters = ["HTTP"],
                    OutboundAdapters = []
                }
            ],
            Modules =
            [
                new TopologyModulePlan
                {
                    Name = "TodoApi.Api",
                    BoundedContextName = "Todos",
                    ProjectName = "TodoApi",
                    Responsibilities = ["Expose todo operations."]
                }
            ],
            Decisions = []
        };
        var contracts = new BoundedContextContractCatalog
        {
            BoundedContextName = "Todos",
            Decisions =
            [
                new StagedArchitectureDecision
                {
                    Title = "Service contract boundary",
                    Decision = "Expose creation through ITodoService.",
                    Reasons = ["Keeps transport independent."],
                    AlternativesRejected = ["Controller-owned behavior"],
                    RelatedPackages = [],
                    AffectedContextNames = ["Todos"]
                }
            ],
            InferredDefaults =
            [
                new DiscoveredDomainDefault
                {
                    Kind = "BestPractice",
                    Subject = "Service lifetime",
                    MissingInformation = "No lifetime was specified.",
                    Decision = "Use a singleton for the stateless service.",
                    Reasons = ["The service has no request state."],
                    Impact = "One service instance is reused.",
                    AffectedCapabilities = ["CreateTodo"],
                    UserOverridable = true
                }
            ],
            Contracts =
            [
                new StagedContract
                {
                    Name = "ITodoService",
                    Kind = "Interface",
                    ModuleName = "TodoApi.Api",
                    Purpose = "Creates todos.",
                    Members = ["Todo Create(string title)"],
                    CapabilityNames = ["CreateTodo"]
                }
            ]
        };
        var components = new BoundedContextComponentManifest
        {
            BoundedContextName = "Todos",
            Decisions = [],
            InferredDefaults = [],
            Components =
            [
                new StagedComponent
                {
                    Name = "TodoService",
                    Kind = "Service",
                    ModuleName = "TodoApi.Api",
                    ProjectName = "TodoApi",
                    Files = ["src/TodoApi/TodoService.cs"],
                    Responsibilities = ["Implement todo creation."],
                    DefinesContractNames = ["ITodoService"],
                    ImplementsPortNames = ["ITodoService"],
                    ConsumesContractNames = [],
                    UsesConcreteComponentNames = [],
                    RegistersImplementationNames = [],
                    TestsComponentNames = [],
                    CapabilityNames = ["CreateTodo"],
                    AcceptanceCriterionIds =
                        ["AC-CREATETODO-CREATE-A-VALID-TODO"],
                    Lifetime = "Singleton",
                    ComplexityPoints = 2,
                    VerificationKinds = ["UnitTest"]
                }
            ]
        };
        return new StagedPlanningArtifacts(
            domain,
            topology,
            [contracts],
            [components]);
    }
}
