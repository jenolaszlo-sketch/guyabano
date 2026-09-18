using System.Text.Json;
using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Shared argument readers for planning stage executors. All readers
/// accept any runtime value representation via
/// <see cref="RuntimeValueJson"/> because the runtime may normalize
/// provider outputs (e.g. JSON to nominal composites for named types).
/// </summary>
internal static class PlanningStageArguments
{
    public static string? ReadString(
        InferenceExecutionRequest request, string name, bool required)
    {
        foreach (var argument in request.Arguments)
        {
            if (string.Equals(argument.Name, name, StringComparison.Ordinal) &&
                argument.Value is not null)
            {
                var json = RuntimeValueJson.ToJsonElement(argument.Value);
                if (json.ValueKind == JsonValueKind.String)
                    return json.GetString();
            }
        }
        if (required)
            throw new InvalidOperationException(
                $"Planning stage inference requires a string '{name}' argument.");
        return null;
    }

    public static JsonElement? ReadJson(
        InferenceExecutionRequest request, string name, bool required)
    {
        foreach (var argument in request.Arguments)
        {
            if (string.Equals(argument.Name, name, StringComparison.Ordinal) &&
                argument.Value is not null)
            {
                return RuntimeValueJson.ToJsonElement(argument.Value);
            }
        }
        if (required)
            throw new InvalidOperationException(
                $"Planning stage inference requires a '{name}' argument.");
        return null;
    }

    public static string ReadActivityString(
        ActivityExecutionRequest request, string name)
    {
        foreach (var argument in request.Arguments)
        {
            if (string.Equals(argument.Name, name, StringComparison.Ordinal) &&
                argument.Value is not null)
            {
                var json = RuntimeValueJson.ToJsonElement(argument.Value);
                if (json.ValueKind == JsonValueKind.String)
                    return json.GetString()!;
            }
        }
        throw new InvalidOperationException(
            $"Planning stage activity requires a string '{name}' argument.");
    }

    public static JsonElement ReadActivityJson(
        ActivityExecutionRequest request, string name)
    {
        foreach (var argument in request.Arguments)
        {
            if (string.Equals(argument.Name, name, StringComparison.Ordinal) &&
                argument.Value is not null)
            {
                return RuntimeValueJson.ToJsonElement(argument.Value);
            }
        }
        throw new InvalidOperationException(
            $"Planning stage activity requires a '{name}' argument.");
    }
}
