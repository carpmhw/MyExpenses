using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyExpenses.Api.Services;

/// <summary>Produces stable SHA-256 hashes for semantically equivalent JSON request payloads.</summary>
public static class IdempotencyRequestHasher
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Computes a lowercase hexadecimal hash from a canonicalized request payload.</summary>
    public static string Compute(object payload)
    {
        var serialized = JsonSerializer.Serialize(payload, SerializerOptions);
        using var document = JsonDocument.Parse(serialized);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(document.RootElement, writer);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    /// <summary>Writes a JSON value with object properties sorted for deterministic hashing.</summary>
    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
