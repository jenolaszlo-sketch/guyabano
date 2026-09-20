using System.Text;

namespace Guyabano.CodeGeneration.Planning;

/// <summary>
/// Shared slug rules for artifact names derived from domain language
/// (bounded-context names and the like). Single implementation so publishers,
/// stage adapters, and services derive identical identities.
/// </summary>
internal static class ArtifactSlugs
{
    public static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "unnamed" : slug;
    }
}
