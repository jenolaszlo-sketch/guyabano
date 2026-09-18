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

    /// <summary>
    /// Schema descriptor for a joint context-design attempt envelope
    /// (<c>{ok, catalog, manifest, error, retryStage, retryArtifact}</c>)
    /// carried as per-context retry-loop state.
    /// </summary>
    public static (DescriptorReference Descriptor, ObjectSchemaDefinition Schema) JointAttemptSchema()
    {
        const string name = "guyabano.context-design-attempt";
        var descriptor = new DescriptorReference(
            DescriptorKind.Schema,
            name,
            Version,
            Digest("descriptor/v1", Encoding.UTF8.GetBytes(
                $"{name}@{Version}|{{ok:Boolean,catalog:Optional(Json),manifest:Optional(Json),error:Optional(String),retryStage:Optional(String),retryArtifact:Optional(Json)}}")));
        var schema = new ObjectSchemaDefinition(
            descriptor,
            [
                new SchemaField("ok", new PrimitiveType(FuwenPrimitiveKind.Boolean)),
                new SchemaField("catalog", new OptionalType(new PrimitiveType(FuwenPrimitiveKind.Json))),
                new SchemaField("manifest", new OptionalType(new PrimitiveType(FuwenPrimitiveKind.Json))),
                new SchemaField("error", new OptionalType(new PrimitiveType(FuwenPrimitiveKind.String))),
                new SchemaField("retryStage", new OptionalType(new PrimitiveType(FuwenPrimitiveKind.String))),
                new SchemaField("retryArtifact", new OptionalType(new PrimitiveType(FuwenPrimitiveKind.Json))),
            ]);
        return (descriptor, schema);
    }

    /// <summary>Attempt-assessment activity pinning its exact contract shape.</summary>
    public static DescriptorReference AssessActivity() => new(
        DescriptorKind.Activity,
        "guyabano.assess-context-design",
        Version,
        Digest("descriptor/v1", Encoding.UTF8.GetBytes(
            $"guyabano.assess-context-design@{Version}|(bundle:Json,contract:Json,manifest:Json)->Json")));

    /// <summary>Gap-resolution activity pinning the resolver pack bytes.</summary>
    public static DescriptorReference GapActivity(byte[] systemPrompt, byte[] userPrompt)
    {
        const string name = "guyabano.resolve-stage-guidance";
        var identity = Encoding.UTF8.GetBytes($"{name}@{Version}|pack=planning-gap-resolution|");
        var pinned = new byte[identity.Length + systemPrompt.Length + 1 + userPrompt.Length];
        Buffer.BlockCopy(identity, 0, pinned, 0, identity.Length);
        Buffer.BlockCopy(systemPrompt, 0, pinned, identity.Length, systemPrompt.Length);
        pinned[identity.Length + systemPrompt.Length] = 0;
        Buffer.BlockCopy(userPrompt, 0, pinned, identity.Length + systemPrompt.Length + 1, userPrompt.Length);
        return new DescriptorReference(DescriptorKind.Activity, name, Version, Digest("descriptor/v1", pinned));
    }

    /// <summary>Guidance-folding activity pinning its exact contract shape.</summary>
    public static DescriptorReference ApplyGuidanceActivity() => new(
        DescriptorKind.Activity,
        "guyabano.apply-guidance",
        Version,
        Digest("descriptor/v1", Encoding.UTF8.GetBytes(
            $"guyabano.apply-guidance@{Version}|(attempt:Json,guidance:String)->Json")));

    /// <summary>Catalog/manifest pair activity pinning its exact contract shape.</summary>
    public static DescriptorReference PairActivity() => new(
        DescriptorKind.Activity,
        "guyabano.pair-artifacts",
        Version,
        Digest("descriptor/v1", Encoding.UTF8.GetBytes(
            $"guyabano.pair-artifacts@{Version}|(catalog:Json,manifest:Json)->Json")));

    /// <summary>Plan-assembly activity pinning its exact contract shape.</summary>
    public static DescriptorReference AssembleActivity() => new(
        DescriptorKind.Activity,
        "guyabano.assemble-plan",
        Version,
        Digest("descriptor/v1", Encoding.UTF8.GetBytes(
            $"guyabano.assemble-plan@{Version}|(domain:Json,topology:Json,catalogs:List,manifests:List)->Json")));

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
    /// (<c>{ok, artifact, error}</c>) shared by every planning stage,
    /// pinning the exact field shape. Retry loops carry this envelope as
    /// state; <c>previousFailure</c> binds the envelope's error back into
    /// the next attempt and the break condition checks its <c>ok</c> flag.
    /// </summary>
    public static (DescriptorReference Descriptor, ObjectSchemaDefinition Schema) StageAttemptSchema()
    {
        const string name = "guyabano.stage-attempt";
        var descriptor = new DescriptorReference(
            DescriptorKind.Schema,
            name,
            Version,
            Digest("descriptor/v1", Encoding.UTF8.GetBytes(
                $"{name}@{Version}|{{ok:Boolean,artifact:Json,error:Optional(String)}}")));
        var schema = new ObjectSchemaDefinition(
            descriptor,
            [
                new SchemaField("ok", new PrimitiveType(FuwenPrimitiveKind.Boolean)),
                new SchemaField("artifact", new PrimitiveType(FuwenPrimitiveKind.Json)),
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
