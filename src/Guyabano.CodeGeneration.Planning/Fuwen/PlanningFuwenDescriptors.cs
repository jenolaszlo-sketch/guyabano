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
    public const string TemplateName = "guyabano.domain-discovery";
    public const string Version = "1";

    /// <summary>Repository-context provider for planning requests.</summary>
    public static DescriptorReference Context() => new(
        DescriptorKind.ContextProvider,
        ContextName,
        Version,
        Digest("descriptor/v1", Encoding.UTF8.GetBytes(
            $"{ContextName}@{Version}|provider=guyabano.planning-context|output=string")));

    /// <summary>
    /// Planner inference profile pinning the exact model, token ceiling,
    /// prompt pack, and stage output schema.
    /// </summary>
    public static DescriptorReference Profile(string model, int maxTokens) => new(
        DescriptorKind.InferenceProfile,
        ProfileName,
        Version,
        Digest("descriptor/v1", Encoding.UTF8.GetBytes(
            $"{ProfileName}@{Version}|model={model}|maxTokens={maxTokens}|pack=domain-discovery|schema=Guyabano.CodeGeneration.Planning.DomainDiscovery")));

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
    /// Prompt template pinning the exact rendered pack bytes
    /// (<c>system.sbn</c> + <c>user.sbn</c>).
    /// </summary>
    public static DescriptorReference Template(byte[] systemPrompt, byte[] userPrompt)
    {
        var pinned = new byte[systemPrompt.Length + 1 + userPrompt.Length];
        Buffer.BlockCopy(systemPrompt, 0, pinned, 0, systemPrompt.Length);
        pinned[systemPrompt.Length] = 0;
        Buffer.BlockCopy(userPrompt, 0, pinned, systemPrompt.Length + 1, userPrompt.Length);
        return new DescriptorReference(
            DescriptorKind.PromptTemplate, TemplateName, Version,
            Digest("descriptor/v1", pinned));
    }

    private static ContentDigest Digest(string contract, byte[] content) => new(
        "sha256",
        contract,
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
}
