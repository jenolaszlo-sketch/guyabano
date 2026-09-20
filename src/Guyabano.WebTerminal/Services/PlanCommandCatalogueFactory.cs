using Guyabano.CodeGeneration.Planning;
using Guyabano.CodeGeneration.Planning.Fuwen;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Guihua;
using Penghou.Guihua.Baize;

namespace Guyabano.WebTerminal.Services;

/// <summary>
/// Builds the production authoring catalogue: the real domain-discovery
/// stage descriptors (context provider, planner profile, prompt template
/// pinned to the exact pack bytes) plus the rendered catalogue summary
/// the authoring pack injects.
/// </summary>
internal static class PlanCommandCatalogueFactory
{
    public static PlanCommandCatalogue Create(string promptsRoot, string model, int maxTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var context = PlanningFuwenDescriptors.Context();
        var profile = PlanningFuwenDescriptors.Profile(model, maxTokens);
        var template = PlanningFuwenDescriptors.Template(
            "domain-discovery",
            "Guyabano.CodeGeneration.Planning.DomainDiscovery",
            File.ReadAllBytes(Path.Combine(promptsRoot, "domain-discovery", "system.sbn")),
            File.ReadAllBytes(Path.Combine(promptsRoot, "domain-discovery", "user.sbn")));
        static CallableContract Contract(FuwenType output, params (string Name, FuwenType Type)[] parameters) => new(
            new CallableSignature([.. parameters.Select(p => new CallableParameter(p.Name, p.Type))], output),
            CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe);
        var optionalBoolean = new OptionalType(new PrimitiveType(FuwenPrimitiveKind.Boolean));
        var optionalInteger = new OptionalType(new PrimitiveType(FuwenPrimitiveKind.Integer));
        var optionalStr = new OptionalType(str);
        var entries = new[]
        {
            new TrustedCatalogueDescriptor(context, callableContract: new CallableContract(
                new CallableSignature(
                    [
                        new CallableParameter("request", str),
                        new CallableParameter("repositoryContext", optionalStr),
                        new CallableParameter("includeRepositoryContext", optionalBoolean),
                        new CallableParameter("maxCharacters", optionalInteger),
                    ],
                    str),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe)),
            new TrustedCatalogueDescriptor(profile, callableContract:
                Contract(str, ("request", str))),
            new TrustedCatalogueDescriptor(template),
        };
        var catalogue = new InMemoryTrustedCatalogue(entries);
        return new PlanCommandCatalogue(catalogue, CatalogueSummaryBuilder.Render(entries));
    }
}
