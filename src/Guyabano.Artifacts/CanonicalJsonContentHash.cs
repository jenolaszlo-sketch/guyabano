using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Guyabano.Artifacts;

/// <summary>
/// Guyabano canonical JSON v2. Objects use ordinal property ordering, arrays
/// preserve order, strings use System.Text.Json UTF-8 escaping, and numbers use
/// normalized decimal or IEEE-754 round-trip representations.
/// </summary>
public static class CanonicalJsonContentHash
{
    public const string Version = "v2";

    public static string Compute(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false
        }))
        {
            WriteCanonical(writer, value);
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan))
            .ToLowerInvariant();
    }

    public static string ComputeEnvelopeContent(JsonElement envelope)
    {
        var reference = envelope.GetProperty("reference");
        var inputIds = envelope.GetProperty("inputs")
            .EnumerateArray()
            .Select(item => item.GetProperty("artifactId").GetString()!)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var content = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["kind"] = reference.GetProperty("kind").GetString(),
                ["schemaVersion"] = reference
                    .GetProperty("schemaVersion").GetInt32(),
                ["status"] = envelope.GetProperty("status"),
                ["inputArtifactIds"] = inputIds,
                ["payload"] = envelope.GetProperty("payload")
            });
        return Compute(content);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                    .OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(NormalizeNumber(value), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException(
                    $"Unsupported JSON token '{value.ValueKind}'.");
        }
    }

    private static string NormalizeNumber(JsonElement value)
    {
        if (value.TryGetDecimal(out var decimalValue))
            return decimalValue == 0
                ? "0"
                : decimalValue.ToString("G29", CultureInfo.InvariantCulture);
        var doubleValue = value.GetDouble();
        if (!double.IsFinite(doubleValue))
            throw new JsonException("Canonical JSON does not support non-finite numbers.");
        if (doubleValue == 0)
            return "0";
        var formatted = doubleValue.ToString("R", CultureInfo.InvariantCulture)
            .Replace("E+", "e", StringComparison.Ordinal)
            .Replace("E", "e", StringComparison.Ordinal);
        var exponent = formatted.IndexOf('e');
        if (exponent >= 0)
        {
            var prefix = formatted[..(exponent + 1)];
            var suffix = formatted[(exponent + 1)..];
        var negative = suffix.StartsWith("-", StringComparison.Ordinal);
            suffix = suffix.TrimStart('+', '-').TrimStart('0');
            if (suffix.Length == 0)
                suffix = "0";
            formatted = prefix + (negative ? "-" : string.Empty) + suffix;
        }
        return formatted;
    }
}
