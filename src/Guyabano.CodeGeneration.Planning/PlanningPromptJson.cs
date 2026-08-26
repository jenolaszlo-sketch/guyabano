using System.Text.Json;

namespace Guyabano.CodeGeneration.Planning;

internal static class PlanningPromptJson
{
    private static readonly JsonSerializerOptions Options = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);
}
