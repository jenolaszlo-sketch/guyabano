using System.Text.Json;
using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>Shared argument readers for planning stage executors.</summary>
internal static class PlanningStageArguments
{
    public static string? ReadString(
        InferenceExecutionRequest request, string name, bool required)
    {
        foreach (var argument in request.Arguments)
        {
            if (string.Equals(argument.Name, name, StringComparison.Ordinal) &&
                argument.Value is JsonRuntimeValue json &&
                json.Value.ValueKind == JsonValueKind.String)
            {
                return json.Value.GetString();
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
                argument.Value is JsonRuntimeValue json)
            {
                return json.Value.Clone();
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
                argument.Value is JsonRuntimeValue json &&
                json.Value.ValueKind == JsonValueKind.String)
            {
                return json.Value.GetString()!;
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
                argument.Value is JsonRuntimeValue json)
            {
                return json.Value.Clone();
            }
        }
        throw new InvalidOperationException(
            $"Planning stage activity requires a '{name}' argument.");
    }
}
