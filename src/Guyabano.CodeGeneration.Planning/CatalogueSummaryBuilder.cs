using System.Globalization;
using System.Text;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>
/// Renders a trusted catalogue as the catalogue summary the workflow
/// authoring pack injects: one line per descriptor with its exact identity
/// and callable signature, so the model references only real entries.
/// </summary>
public static class CatalogueSummaryBuilder
{
    public static string Render(IEnumerable<TrustedCatalogueDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var builder = new StringBuilder();
        foreach (var entry in descriptors)
        {
            var reference = entry.Descriptor;
            builder.Append(CultureInfo.InvariantCulture,
                $"{reference.Kind} {reference.Name}@{reference.Version}#{reference.ContentDigest.Value}");
            if (entry.CallableContract is not null)
            {
                var signature = entry.CallableContract.Signature;
                var parameters = string.Join(", ", signature.Parameters.Select(parameter =>
                    $"{parameter.Name}: {Describe(parameter.Type)}"));
                builder.Append(CultureInfo.InvariantCulture, $" ({parameters}) -> {Describe(signature.OutputType)}");
            }
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string Describe(FuwenType type) => type switch
    {
        PrimitiveType primitive => primitive.Primitive switch
        {
            FuwenPrimitiveKind.String => "string",
            FuwenPrimitiveKind.Integer => "integer",
            FuwenPrimitiveKind.Number => "number",
            FuwenPrimitiveKind.Boolean => "boolean",
            FuwenPrimitiveKind.Duration => "duration",
            _ => "Json",
        },
        OptionalType optional => Describe(optional.ValueType) + "?",
        ListType list => $"list<{Describe(list.ItemType)}>[{list.MaxItems}]",
        NamedTypeReference named => named.Schema.Name,
        ArtifactType => "artifact",
        _ => "Json",
    };
}
