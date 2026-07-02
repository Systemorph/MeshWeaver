using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Domain;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// Custom converter for object types that handles polymorphic serialization/deserialization
/// using the type registry and $type discriminator.
/// </summary>
public class ObjectPolymorphicConverter(ITypeRegistry typeRegistry, ILogger? logger = null) : JsonConverter<object>
{
    // Warn at most once per unregistered $type (per hub/options instance) so the receiving-hub diagnostic
    // can never become a log storm. Instance field — dies with the hub's serializer options (no static state).
    // Bounded: '$type' is payload-controlled, so the dedup set is capped (adversarial junk types stop being
    // deduped past the cap — they log per occurrence, which is visible, not a memory-DoS) and very long
    // names are truncated before caching/logging.
    private const int MaxWarnedTypeNames = 1000;
    private const int MaxTypeNameLogLength = 256;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> warnedUnregisteredRead = new();
    private int warnCapNoticed;

    private void WarnUnregisteredDeserialization(string typeName)
    {
        if (logger is null)
            return;
        if (typeName.Length > MaxTypeNameLogLength)
            typeName = typeName[..MaxTypeNameLogLength] + "… (truncated)";
        if (warnedUnregisteredRead.Count >= MaxWarnedTypeNames)
        {
            // Cap reached: stop caching (bounded memory) AND stop logging (bounded log volume) —
            // one notice marks the transition. Real systems have dozens of distinct types; only
            // payload-controlled junk '$type' storms ever get here.
            if (System.Threading.Interlocked.Exchange(ref warnCapNoticed, 1) == 0)
                logger.LogWarning(
                    "Unregistered-$type warning cap ({Max} distinct names) reached — further distinct "
                    + "unregistered '$type' values will not be logged (payload-controlled junk?).",
                    MaxWarnedTypeNames);
            return;
        }
        if (!warnedUnregisteredRead.TryAdd(typeName, 0))
            return;
        logger.LogWarning(
            "Received '$type':'{TypeName}' which is NOT registered in this (receiving) hub — it can only be read "
            + "as an untyped JsonElement (the value renders empty / reactive waits time out). Register it in this "
            + "hub too (WithType(typeof({TypeName}), nameof({TypeName}))): a $type must be registered in the "
            + "RECEIVING hub as well as the sending one.",
            typeName, typeName, typeName);
    }

    /// <summary>
    /// Determines whether this converter applies. It only handles the open <see langword="object"/>
    /// type; concrete types are handled by their own type info.
    /// </summary>
    /// <param name="typeToConvert">The type being considered for conversion.</param>
    /// <returns><c>true</c> only when <paramref name="typeToConvert"/> is exactly <see langword="object"/>.</returns>
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert == typeof(object);
    }

    /// <summary>
    /// Reads a value typed as <see langword="object"/>. Scalars (string, number, bool, null) are
    /// materialized as their CLR primitives, arrays as a cloned <see cref="JsonElement"/>, and objects
    /// are resolved via the $type discriminator against the type registry (falling back to a cloned
    /// <see cref="JsonElement"/> when no/unknown discriminator is present).
    /// </summary>
    /// <param name="reader">The reader positioned at the value to deserialize.</param>
    /// <param name="typeToConvert">The declared target type (<see langword="object"/>).</param>
    /// <param name="options">The serializer options in effect.</param>
    /// <returns>The materialized value, or <c>null</c> for a JSON null token.</returns>
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Handle different JSON token types
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                // Try to determine if it's an integer or decimal
                if (reader.TryGetInt32(out var intValue))
                    return intValue;
                if (reader.TryGetInt64(out var longValue))
                    return longValue;
                return reader.GetDouble();
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.StartObject:
                return ReadObject(ref reader, options);
            case JsonTokenType.StartArray:
                // For arrays, deserialize as JsonElement and let the application handle it
                using (var doc = JsonDocument.ParseValue(ref reader))
                {
                    return doc.RootElement.Clone();
                }
            default:
                throw new JsonException($"Unexpected token type: {reader.TokenType}");
        }
    }
    private object ReadObject(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // Strip metadata properties like $id that can cause positioning issues
        var cleanedElement = StripMetadataProperties(root);

        // Check if this object has a type discriminator
        if (cleanedElement.TryGetProperty(EntitySerializationExtensions.TypeProperty, out var typeElement))
        {
            var typeName = typeElement.GetString();
            if (!string.IsNullOrEmpty(typeName) && typeRegistry.TryGetType(typeName, out var typeInfo))
            {
                try
                {
                    // Deserialize to the specific type using cleaned JSON
                    // Normalize to ensure $type is first (required for parameterized constructor types)
                    var json = JsonElementNormalizer.GetNormalizedRawText(cleanedElement);
                    return JsonSerializer.Deserialize(json, typeInfo!.Type, options)!;
                }
                catch (Exception ex) when (
                    ex is JsonException
                    or NotSupportedException
                    or InvalidOperationException
                    or ArgumentException)
                {
                    // Registered type but the stored JSON no longer fits it. Don't throw
                    // (a throw faults the node read → wedged grain); preserve the raw JSON
                    // so the node stays readable/repairable. Logged loud, not swallowed.
                    logger?.LogWarning(ex,
                        "Content for '{TypeName}' could not be deserialized; preserving raw JSON",
                        typeName);
                    return cleanedElement.Clone();
                }
            }
            else if (!string.IsNullOrEmpty(typeName))
            {
                // The payload carries a $type but THIS (receiving) hub has no registration for it, so it can
                // only be read back as an untyped JsonElement (renders empty / reactive waits time out — the
                // chat-vanish / untyped-storm class). A type must be registered in the RECEIVING hub as well
                // as the sending one; warn once per type so the missing registration is found fast.
                WarnUnregisteredDeserialization(typeName!);
            }
        }

        // If no type discriminator or unknown type, return as JsonElement
        return cleanedElement.Clone();
    }

    private static JsonElement StripMetadataProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return element;

        var metadataProps = new[] { "$id", "$ref", "$values", "$defs" };
        var hasMetadata = false;

        foreach (var prop in metadataProps)
        {
            if (element.TryGetProperty(prop, out _))
            {
                hasMetadata = true;
                break;
            }
        }

        if (!hasMetadata)
            return element;

        // Create a new object without metadata properties
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        foreach (var property in element.EnumerateObject())
        {
            if (!metadataProps.Contains(property.Name))
            {
                property.WriteTo(writer);
            }
        }
        writer.WriteEndObject();
        writer.Flush();

        var jsonBytes = stream.ToArray();
        using var doc = JsonDocument.Parse(jsonBytes);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Writes a value typed as <see langword="object"/>. Primitives are written directly; ValueTuples
    /// and registered complex types are wrapped with a $type discriminator so they can be read back
    /// polymorphically; types with a dedicated converter are delegated to it; unregistered types are
    /// serialized without a discriminator.
    /// </summary>
    /// <param name="writer">The writer to emit JSON to.</param>
    /// <param name="value">The value to serialize; <c>null</c> is written as a JSON null.</param>
    /// <param name="options">The serializer options in effect.</param>
    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value is JsonElement je)
        {
            je.WriteTo(writer);
            return;
        }

        var valueType = value.GetType();

        // For primitive types, write directly
        if (valueType == typeof(string))
        {
            writer.WriteStringValue((string)value);
        }
        else if (valueType == typeof(int))
        {
            writer.WriteNumberValue((int)value);
        }
        else if (valueType == typeof(long))
        {
            writer.WriteNumberValue((long)value);
        }
        else if (valueType == typeof(double))
        {
            writer.WriteNumberValue((double)value);
        }
        else if (valueType == typeof(float))
        {
            writer.WriteNumberValue((float)value);
        }
        else if (valueType == typeof(decimal))
        {
            writer.WriteNumberValue((decimal)value);
        }
        else if (valueType == typeof(bool))
        {
            writer.WriteBooleanValue((bool)value);
        }
        else
        {
            // Special handling for ValueTuple types - they need type information for proper deserialization
            if (IsValueTuple(valueType))
            {
                // Register the ValueTuple type and serialize with type information
                var typeName = typeRegistry.GetOrAddType(valueType);
                var json = JsonSerializer.Serialize(value, valueType, options);
                using var doc = JsonDocument.Parse(json);

                writer.WriteStartObject();
                writer.WriteString(EntitySerializationExtensions.TypeProperty, typeName);

                // For ValueTuple, copy the properties from the serialized JSON
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        if (property.Name != EntitySerializationExtensions.TypeProperty)
                        {
                            property.WriteTo(writer);
                        }
                    }
                }
                else
                {
                    // If it's not an object (shouldn't happen for ValueTuple), serialize as value
                    writer.WritePropertyName("Value");
                    doc.WriteTo(writer);
                }

                writer.WriteEndObject();
            }
            // Check if this type has a specific converter that should handle it
            else if (HasSpecificConverter(valueType, options))
            {
                // Let the specific converter handle it without adding type information
                JsonSerializer.Serialize(writer, value, valueType, options);
            }
            // For complex types, serialize with type information if registered
            else if (typeRegistry.TryGetCollectionName(valueType, out var typeName))
            {
                // Create a wrapper object with type discriminator
                var json = JsonSerializer.Serialize(value, valueType, options);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    writer.WriteStartObject();
                    writer.WriteString(EntitySerializationExtensions.TypeProperty, typeName);

                    // Copy all properties from the original object
                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        if (property.Name != EntitySerializationExtensions.TypeProperty)
                        {
                            property.WriteTo(writer);
                        }
                    }

                    writer.WriteEndObject();
                }
                else
                {
                    // For non-object types, just serialize normally
                    JsonSerializer.Serialize(writer, value, valueType, options);
                }
            }
            else
            {
                // Type not registered, serialize without type information
                JsonSerializer.Serialize(writer, value, valueType, options);
            }
        }
    }
    private static bool HasSpecificConverter(Type valueType, JsonSerializerOptions options)
    {
        // Check if there's a specific converter for this type
        foreach (var converter in options.Converters)
        {
            if (converter.CanConvert(valueType) && converter.GetType() != typeof(ObjectPolymorphicConverter))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsValueTuple(Type type)
    {
        if (!type.IsGenericType)
            return false;

        var genericTypeDefinition = type.GetGenericTypeDefinition();
        return genericTypeDefinition == typeof(ValueTuple<>) ||
               genericTypeDefinition == typeof(ValueTuple<,>) ||
               genericTypeDefinition == typeof(ValueTuple<,,>) ||
               genericTypeDefinition == typeof(ValueTuple<,,,>) ||
               genericTypeDefinition == typeof(ValueTuple<,,,,>) ||
               genericTypeDefinition == typeof(ValueTuple<,,,,,>) ||
               genericTypeDefinition == typeof(ValueTuple<,,,,,,>) ||
               genericTypeDefinition == typeof(ValueTuple<,,,,,,,>);
    }
}
