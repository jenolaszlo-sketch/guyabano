using System.Text.Json;
using System.Text.Json.Nodes;
using Penghou.Fuwen;

namespace Guyabano.CodeGeneration.Planning.Fuwen;

/// <summary>
/// Converts any runtime value representation to detached JSON. The Zhinu
/// runtime may normalize provider outputs (e.g. JSON to nominal composite
/// values when the node declares a named output type), so stage executors
/// must never assume <see cref="JsonRuntimeValue"/> arguments.
/// </summary>
internal static class PlanningStageValues
{
    public static JsonElement ToJsonElement(RuntimeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Note: JsonNode.Parse returns a null reference for JSON null, so
        // a null node must be mapped back explicitly instead of dereferenced.
        var node = ToNode(value);
        if (node is null)
        {
            using var document = JsonDocument.Parse("null");
            return document.RootElement.Clone();
        }
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    private static JsonNode? ToNode(RuntimeValue value) => value switch
    {
        JsonRuntimeValue json => JsonNode.Parse(json.Value.GetRawText())!,
        ListRuntimeValue list => ToArray(list),
        ObjectRuntimeValue @object => ToObject(@object),
        ArtifactRuntimeValue artifact => ToArtifact(artifact),
        _ => throw new InvalidOperationException(
            $"Unsupported runtime value kind '{value.GetType().Name}'."),
    };

    private static JsonArray ToArray(ListRuntimeValue list)
    {
        var array = new JsonArray();
        foreach (var item in list.Items)
            array.Add(ToNode(item));
        return array;
    }

    private static JsonObject ToObject(ObjectRuntimeValue @object)
    {
        var result = new JsonObject();
        foreach (var property in @object.Properties)
            result[property.Key] = ToNode(property.Value);
        return result;
    }

    private static JsonObject ToArtifact(ArtifactRuntimeValue artifact) => new()
    {
        ["$kind"] = "artifact",
        ["provider"] = artifact.Artifact.Provider,
        ["artifactId"] = artifact.Artifact.ArtifactId,
    };
}
