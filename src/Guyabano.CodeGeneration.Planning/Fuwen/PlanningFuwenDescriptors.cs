using System.Security.Cryptography;
using System.Text;
using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Real Fuwen catalogue identities for Guyabano's domain-discovery planning
/// stage. Digests honestly pin content: the prompt-template digest is the
/// SHA-256 of the exact <c>domain-discovery</c> pack bytes, so editing a
/// prompt changes the descriptor identity and forces re-admission; the
/// profile digest pins the planner model, token ceiling, and stage schema.
/// </summary>
public static class PlanningFuwenDescriptors
{
    public const string ContextName = "guyabano.planning-context";
    public const string ProfileName = "guyabano.planner-profile";
    public const string Version = "1";

    public static string TemplateName(string pack) => $"guyabano.{pack}";

    /// <summary>
    /// Schema descriptor for a fan-out bundle envelope
    /// (<c>{name, bundle}</c>) pinning the exact field shape. Fan-out keys
    /// must statically resolve to string/integer/enum, so items travel in
    /// this envelope with the context name projected as the key.
    /// </summary>
    public static (DescriptorReference Descriptor, ObjectSchemaDefinition Schema) BundleSchema()
    {
        const string name = "guyabano.contract-bundle";
        var descriptor = new DescriptorReference(
            DescriptorKind.Schema,
            name,
            Version,
            Digest("descriptor/v1", Encoding.UTF8.GetBytes(
                $"{name}@{Version}|{{name:String,bundle:Json}}")));
        var schema = new ObjectSchemaDefinition(
            descriptor,
            [
                new SchemaField("name", new PrimitiveType(FuwenPrimitiveKind.String)),
                new SchemaField("bundle", new PrimitiveType(FuwenPrimitiveKind.Json)),
            ]);
        return (descriptor, schema);
    }

    /// <summary>Bundle activity pinning its exact contract shape.</summary>
    public static DescriptorReference BundleActivity() => new(
        DescriptorKind.Activity,
        "guyabano.bundle-contract-inputs",
        Version,
        Digest("descriptor/v1", Encoding.UTF8.GetBytes(
            $"guyabano.bundle-contract-inputs@{Version}|(topology:Json,domain:Json)->list<Json>[8]")));

    /// <summary>Repository-context provider for planning requests.</summary>
    public static DescriptorReference Context() => new(
        DescriptorKind.ContextProvider,
        ContextName,
        Version,
        Digest("descriptor/v1", Encoding.UTF8.GetBytes(
            $"{ContextName}@{Version}|provider=guyabano.planning-context|output=string")));

    /// <summary>
    /// Planner inference profile pinning the exact model and token ceiling.
    /// Stage specifics (pack, schema) live on the prompt template.
    /// </summary>
    public static DescriptorReference Profile(string model, int maxTokens) =>
        StageProfile("domain-discovery", model, maxTokens);

    /// <summary>
    /// Per-stage planner profile. Stages need distinct descriptors because
    /// each stage declares its own callable signature.
    /// </summary>
    public static DescriptorReference StageProfile(string stage, string model, int maxTokens)
    {
        var name = $"{ProfileName}-{stage}";
        return new DescriptorReference(
            DescriptorKind.InferenceProfile,
            name,
            Version,
            Digest("descriptor/v1", Encoding.UTF8.GetBytes(
                $"{name}@{Version}|model={model}|maxTokens={maxTokens}|stage={stage}")));
    }

    /// <summary>
    /// Schema descriptor for a single stage-attempt envelope
    /// (<c>{ok, domain, error}</c>) pinning the exact field shape.
    /// </summary>
    public static (DescriptorReference Descriptor, ObjectSchemaDefinition Schema) AttemptSchema()
    {
        const string name = "guyabano.planning-attempt";
        var descriptor = new DescriptorReference(
            DescriptorKind.Schema,
            name,
            Version,
            Digest("descriptor/v1", Encoding.UTF8.GetBytes(
                $"{name}@{Version}|{{ok:Boolean,domain:Json,error:Optional(String)}}")));
        var schema = new ObjectSchemaDefinition(
            descriptor,
            [
                new SchemaField("ok", new PrimitiveType(FuwenPrimitiveKind.Boolean)),
                new SchemaField("domain", new PrimitiveType(FuwenPrimitiveKind.Json)),
                new SchemaField("error", new OptionalType(new PrimitiveType(FuwenPrimitiveKind.String))),
            ]);
        return (descriptor, schema);
    }

    /// <summary>
    /// Prompt template pinning the stage pack name, the stage output schema,
    /// and the exact rendered pack bytes (<c>system.sbn</c> + <c>user.sbn</c>).
    /// </summary>
    public static DescriptorReference Template(
        string pack, string schemaName, byte[] systemPrompt, byte[] userPrompt)
    {
        var identity = Encoding.UTF8.GetBytes($"{TemplateName(pack)}@{Version}|pack={pack}|schema={schemaName}|");
        var pinned = new byte[identity.Length + systemPrompt.Length + 1 + userPrompt.Length];
        Buffer.BlockCopy(identity, 0, pinned, 0, identity.Length);
        Buffer.BlockCopy(systemPrompt, 0, pinned, identity.Length, systemPrompt.Length);
        pinned[identity.Length + systemPrompt.Length] = 0;
        Buffer.BlockCopy(userPrompt, 0, pinned, identity.Length + systemPrompt.Length + 1, userPrompt.Length);
        return new DescriptorReference(
            DescriptorKind.PromptTemplate, TemplateName(pack), Version,
            Digest("descriptor/v1", pinned));
    }

    private static ContentDigest Digest(string contract, byte[] content) => new(
        "sha256",
        contract,
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
}
