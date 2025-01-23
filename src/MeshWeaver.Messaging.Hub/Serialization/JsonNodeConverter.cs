﻿using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MeshWeaver.Messaging.Serialization;

public class JsonNodeConverter : JsonConverter<JsonNode>
{
    public override bool CanConvert(Type typeToConvert)
        => typeof(JsonNode).IsAssignableFrom(typeToConvert);

    public override JsonNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    public override void Write(Utf8JsonWriter writer, JsonNode value, JsonSerializerOptions options)
    {
        if(value is not null)
            value.WriteTo(writer);
        else
            writer.WriteNullValue();
    }
}
