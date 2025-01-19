using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Activities;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Layout.Client;

public static class LayoutClientExtensions
{
    public static void UpdatePointer<T>(this ISynchronizationStream<JsonElement> stream, T value,
        string dataContext,
        JsonPointerReference reference, ModelParameter model = null)
    {
        if (reference != null)
        {
            if (model != null)
            {
                var patch = stream.GetPatch(value, reference, string.Empty, model.Element);
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
        var pointer = JsonPointer.Parse(GetPointer(reference.Pointer, dataContext));

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

    public static IObservable<T> DataBind<T>(this ISynchronizationStream<JsonElement> stream,
        JsonPointerReference reference, 
        string dataContext = null, 
        Func<object, T> conversion = null) =>
        stream.GetStream<object>(JsonPointer.Parse(GetPointer(reference.Pointer, dataContext)))
            .Select(conversion ?? (x => stream.ConvertSingle<T>(x, null)));


    public static JsonElement? GetValueFromModel(this ModelParameter model, JsonPointerReference reference)
    {
        var pointer = JsonPointer.Parse($"/{reference.Pointer}");
        return pointer.Evaluate(model.Element);
    }

    public static T GetDataBoundValue<T>(this ISynchronizationStream<JsonElement> stream, object value, string dataContext)
    {
        if (value is JsonPointerReference reference)
            return reference.Pointer.StartsWith('/')
                ? stream.GetDataBoundValue<T>(reference.Pointer)
                : stream.GetDataBoundValue<T>($"{dataContext}/{reference.Pointer}");

        if (value is string stringValue && typeof(T).IsEnum)
            return (T)Enum.Parse(typeof(T), stringValue);

        // Use Convert.ChangeType for flexible conversion
        return (T)Convert.ChangeType(value, typeof(T));
    }
    private static T GetDataBoundValue<T>(this ISynchronizationStream<JsonElement> stream, string pointer, string dataContext = null)
    {
        var jsonPointer = JsonPointer.Parse(GetPointer(pointer, dataContext));

        if (stream.Current == null)
            return default;
        var ret = jsonPointer.Evaluate(stream.Current.Value);
        if (ret == null)
            return default;
        return ret.Value.Deserialize<T>(stream.Hub.JsonSerializerOptions);
    }

    public static IObservable<T> GetDataBoundObservable<T>(this ISynchronizationStream<JsonElement> stream,
        JsonPointerReference reference, string dataContext = null)
    {
        var pointer = GetPointer(reference.Pointer, dataContext);
        var jsonPointer = JsonPointer.Parse(pointer);
        return stream.Select(s =>
        {
            var ret = jsonPointer.Evaluate(s.Value);
            return ret is null ? default(T) : ret.Value.Deserialize<T>(stream.Hub.JsonSerializerOptions);
        });
    }

    private static string GetPointer(string pointer, string dataContext)
    {
        return pointer.StartsWith('/')? pointer.TrimEnd('/') : $"{dataContext}/{pointer.TrimEnd('/')}";
    }

    public static T ConvertSingle<T>(this ISynchronizationStream<JsonElement> stream, object value, Func<object, T> conversion)
    {
        if (conversion != null)
            return conversion.Invoke(value);
        return value switch
        {
            null => default,
            JsonElement element => stream.ConvertJson(element, conversion),
            JsonObject obj => stream.ConvertJson(obj, conversion),
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
    public static async Task<ActivityLog> SubmitModel(this ISynchronizationStream stream, ModelParameter data)
    {
        try
        {
            var response = await stream.Hub.AwaitResponse(
                new DataChangeRequest { Updates = [data.Submit()] },
                o => o.WithTarget(stream.Owner));
            if (response.Message.Status == DataChangeStatus.Committed)
            {
                data.Confirm();
                return response.Message.Log;
            }
            else
                return response.Message.Log;
        }
        catch (Exception e)
        {
            return new ActivityLog(ActivityCategory.DataUpdate)
            {
                End = DateTime.UtcNow,
                Messages = [new(e.Message, LogLevel.Error)]
            };
        }

    }


}
