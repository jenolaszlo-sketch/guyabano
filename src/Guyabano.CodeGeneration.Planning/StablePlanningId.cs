using System.Text;

namespace Guyabano.CodeGeneration.Planning;

internal static class StablePlanningId
{
    public static string Create(string prefix, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var result = new StringBuilder();
        var separatorPending = false;
        foreach (var character in value.Normalize())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separatorPending && result.Length > 0)
                    result.Append('-');
                result.Append(char.ToUpperInvariant(character));
                separatorPending = false;
            }
            else
            {
                separatorPending = true;
            }
        }

        if (result.Length == 0)
            throw new InvalidOperationException(
                $"Unable to create a stable ID from '{value}'.");
        return $"{prefix}-{result}";
    }
}
