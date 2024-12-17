using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;

namespace MeshWeaver.Layout.Client;

public static class LayoutClientExtensions
{
    public static void UpdatePointer<T>(this ISynchronizationStream<JsonElement> stream, T value,
        JsonPointerReference reference, string dataContext, ModelParameter model = null)
    {
        if (reference != null)
        {
            if (model != null)
            {
                var patch = stream.GetPatch(value, reference, dataContext, model.Element);
                if (patch != null)
                    model.Update(patch);
            }

            else
                stream.UpdateAsync(ci =>
                {
                    var patch = stream.GetPatch(value, reference, dataContext, ci);
                    var updated = patch?.Apply(ci) ?? ci;

                    return stream.ToChangeItem(ci, updated, patch, stream.StreamId);
                });

        }
    }

    private static JsonPatch GetPatch<T>(this ISynchronizationStream<JsonElement> stream,
        T value,
        JsonPointerReference reference,
        string dataContext,
        JsonElement current)
    {
        var pointer = JsonPointer.Parse($"{dataContext}{reference.Pointer}");

        var existing = pointer.Evaluate(current);
        if (value == null)
            return existing == null
                ? null
                : new JsonPatch(PatchOperation.Remove(pointer));

        var valueSerialized = JsonSerializer.SerializeToNode(value, stream.Hub.JsonSerializerOptions);

        return existing == null
                ? new JsonPatch(PatchOperation.Add(pointer, valueSerialized))
                : new JsonPatch(PatchOperation.Replace(pointer, valueSerialized))
            ;
    }

    public static IObservable<T> DataBind<T>(this ISynchronizationStream<JsonElement> stream, string dataContext, JsonPointerReference reference, Func<object, T> conversion = null) =>
        stream.GetStream<object>(JsonPointer.Parse($"{dataContext}{reference.Pointer}"))
            .Select(conversion ?? (x => stream.ConvertSingle<T>(x, null)));


    public static JsonElement? GetValueFromModel(this ModelParameter model, JsonPointerReference reference)
    {
        var pointer = JsonPointer.Parse(reference.Pointer);
        return pointer.Evaluate(model.Element);
    }

    public static T GetDataBoundValue<T>(this ISynchronizationStream<JsonElement> stream, object value)
    {
        if (value is JsonPointerReference reference)
            return stream.GetDataBoundValue<T>(reference);

        if (value is string stringValue && typeof(T).IsEnum)
            return (T)Enum.Parse(typeof(T), stringValue);

        // Use Convert.ChangeType for flexible conversion
        return (T)Convert.ChangeType(value, typeof(T));
    }
    private static T GetDataBoundValue<T>(this ISynchronizationStream<JsonElement> stream, JsonPointerReference reference)
    {
        var jsonPointer = JsonPointer.Parse(reference.Pointer);

        if (stream.Current == null)
            return default;
        var ret = jsonPointer.Evaluate(stream.Current.Value);
        if (ret == null)
            return default;
        return ret.Value.Deserialize<T>(stream.Hub.JsonSerializerOptions);
    }

    public static IObservable<T> GetDataBoundObservable<T>(this ISynchronizationStream<JsonElement> stream,
        JsonPointerReference reference)
    {
        var jsonPointer = JsonPointer.Parse(reference.Pointer);
        return stream.Select(s =>
        {
            var ret = jsonPointer.Evaluate(s.Value);
            return ret is null ? default(T) : ret.Value.Deserialize<T>(stream.Hub.JsonSerializerOptions);
        });
    }

    public static T ConvertSingle<T>(this ISynchronizationStream<JsonElement> stream, object value, Func<object, T> conversion)
    {
        if (conversion != null)
            return conversion.Invoke(value);
        return value switch
        {
            null => default,
            JsonElement element => stream.ConvertJson(element, conversion),
            JsonObject obj => stream.ConvertJson<T>(obj, conversion),
            T t => t,
            string s => ConvertString<T>(s),
            _ => throw new InvalidOperationException($"Cannot convert {value} to {typeof(T)}")
        };
    }

    private static T ConvertString<T>(string s)
    {
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (targetType.IsEnum)
            return (T)Enum.Parse(targetType, s);
        return Type.GetTypeCode(targetType) switch
        {
            TypeCode.Int32 => (T)(object)int.Parse(s),
            TypeCode.Double => (T)(object)double.Parse(s),
            TypeCode.Single => (T)(object)float.Parse(s),
            TypeCode.Boolean => (T)(object)bool.Parse(s),
            TypeCode.Int64 => (T)(object)long.Parse(s),
            TypeCode.Int16 => (T)(object)short.Parse(s),
            TypeCode.Byte => (T)(object)byte.Parse(s),
            TypeCode.Char => (T)(object)char.Parse(s),
            _ => throw new InvalidOperationException($"Cannot convert {s} to {typeof(T)}")
        };

    }

    private static T ConvertJson<T>(this ISynchronizationStream<JsonElement> stream, JsonElement? value, Func<object, T> conversion)
    {
        if (value == null)
            return default;
        if (conversion != null)
            return conversion(JsonSerializer.Deserialize<object>(value.Value.GetRawText(), stream.Hub.JsonSerializerOptions));
        return JsonSerializer.Deserialize<T>(value.Value.GetRawText(), stream.Hub.JsonSerializerOptions);
    }
    private static T ConvertJson<T>(this ISynchronizationStream<JsonElement> stream, JsonObject value, Func<object, T> conversion)
    {
        if (value == null)
            return default;
        if (conversion != null)
            return conversion(value.Deserialize<object>(stream.Hub.JsonSerializerOptions));
        return value.Deserialize<T>(stream.Hub.JsonSerializerOptions);
    }

}
