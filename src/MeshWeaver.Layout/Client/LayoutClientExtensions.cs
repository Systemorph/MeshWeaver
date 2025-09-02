using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Patch;
using Json.Pointer;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Layout.Client;

public static class LayoutClientExtensions
{
    public static void UpdatePointer(this ISynchronizationStream<JsonElement> stream, 
        object? value,
        string? dataContext,
        JsonPointerReference? reference, ModelParameter<JsonElement>? model = null)
    {
        if (reference is not null)
        {
            if (model is not null)
            {
                var patch = stream.GetPatch(value, reference, string.Empty, model.Element);
                if (patch != null)
                    model.Update(patch);
            }

            else
                stream.Update(ci =>
                {
                    var patch = stream.GetPatch(value, reference, dataContext, ci);
                    var updated = patch?.Apply(ci) ?? ci;

                    return stream.ToChangeItem(ci, updated, patch!, stream.StreamId);
                },
                    ex =>
                    {
                        stream.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
                            .CreateLogger(typeof(LayoutClientExtensions)).LogWarning(ex, "Cannot update layout");
                        return Task.CompletedTask;
                    });

        }
    }

    private static JsonPatch? GetPatch<T>(this ISynchronizationStream<JsonElement> stream,
        T value,
        JsonPointerReference reference,
        string? dataContext,
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
        string? dataContext = null, 
        Func<object?,T?, T?>? conversion = null,
        T? defaultValue = default(T)) =>
        stream.GetStream<object>(JsonPointer.Parse(GetPointer(reference.Pointer, dataContext ?? "")))
            .Select(x => 
                conversion is not null 
                    ? conversion.Invoke(x, defaultValue) 
                    : stream.Hub.ConvertSingle(x, null, defaultValue!))
            .Where(x => x is not null)
            .Select(x => (T)x!)
            .DistinctUntilChanged();


    public static JsonElement? GetValueFromModel(this ModelParameter<JsonElement> model, JsonPointerReference reference)
    {
        var pointer = JsonPointer.Parse($"/{reference.Pointer}");
        return pointer.Evaluate(model.Element);
    }

    public static T? GetDataBoundValue<T>(this ISynchronizationStream<JsonElement> stream, object? value, string? dataContext)
    {
        if (value is JsonPointerReference reference)
            return reference.Pointer.StartsWith('/')
                ? stream.GetDataBoundValue<T>(reference.Pointer)
                : stream.GetDataBoundValue<T>($"{dataContext}/{reference.Pointer}");

        if (value is string stringValue && typeof(T).IsEnum)
            return (T)Enum.Parse(typeof(T), stringValue);

        if (value is null)
            return default;

        // Use Convert.ChangeType for flexible conversion
        return (T?)Convert.ChangeType(value, typeof(T));
    }
    private static T? GetDataBoundValue<T>(this ISynchronizationStream<JsonElement> stream, string pointer, string? dataContext = null)
    {
        var jsonPointer = JsonPointer.Parse(GetPointer(pointer, dataContext!));

        if (stream.Current == null)
            return default;
        var ret = jsonPointer.Evaluate(stream.Current.Value);
        if (ret == null)
            return default;
        return ret.Value.Deserialize<T>(stream.Hub.JsonSerializerOptions);
    }

    public static IObservable<T> GetDataBoundObservable<T>(this ISynchronizationStream<JsonElement> stream,
        JsonPointerReference reference, string? dataContext = null)
    {
        var pointer = GetPointer(reference.Pointer, dataContext!);
        var jsonPointer = JsonPointer.Parse(pointer);
        return stream.Select(s =>
        {
            var ret = jsonPointer.Evaluate(s.Value);
            return ret is null ? default : ret.Value.Deserialize<T>(stream.Hub.JsonSerializerOptions);
        })
        .Where(x => x is not null)
        .Select(x => x!);
    }

    private static string GetPointer(string pointer, string? dataContext)
    {
        if (pointer.StartsWith('/'))
            return pointer.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(dataContext))
            return $"/{pointer}";
        return $"{dataContext}/{pointer.TrimEnd('/')}";
    }

    public static T? ConvertSingle<T>(this IMessageHub hub, object? value, Func<object?, T?,T?>? conversion, T? defaultValue = default(T))
    {
        conversion ??= null;
        if (conversion != null)
            return conversion.Invoke(value, defaultValue);
        return value switch
        {
            null => defaultValue,
            // ReSharper disable ExpressionIsAlwaysNull
            JsonElement element => hub.ConvertJson(element, conversion, defaultValue),
            JsonObject obj => hub.ConvertJson(obj, conversion, defaultValue),
            JsonNode node => node.Deserialize<T>(hub.JsonSerializerOptions),
            // ReSharper restore ExpressionIsAlwaysNull
            T t => t,
            string s => ConvertString<T>(s),
            _ => ConvertNullableOrNumericValue(value, defaultValue)
        };
    }

    private static T? ConvertNullableOrNumericValue<T>(object? value, T? defaultValue)
    {
        // Handle nullable source types - extract the underlying value if it has one
        if (value != null)
        {
            var valueType = value.GetType();
            if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                // This is a nullable type - check if it has a value
                var underlyingValue = valueType.GetProperty("Value")?.GetValue(value);
                var hasValue = (bool)(valueType.GetProperty("HasValue")?.GetValue(value) ?? false);
                
                if (hasValue && underlyingValue != null)
                {
                    // Use the underlying value for conversion
                    return ConvertNumericValue<T>(underlyingValue);
                }
                else
                {
                    // Nullable has no value - return default
                    return defaultValue!;
                }
            }
        }
        
        // Not a nullable type, proceed with normal numeric conversion
        return ConvertNumericValue<T>(defaultValue);
    }

    private static T? ConvertNumericValue<T>(object? value)
    {
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        
        // Handle numeric conversions more safely
        if (IsNumericType(targetType))
        {
            return ConvertNumericSafely<T>(value, targetType);
        }
        
        // Fall back to Convert.ChangeType for non-numeric types
        return (T?)Convert.ChangeType(value, typeof(T));
    }

    private static bool IsNumericType(Type type)
    {
        return Type.GetTypeCode(type) switch
        {
            TypeCode.Byte or TypeCode.SByte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64 or 
            TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 or 
            TypeCode.Decimal or TypeCode.Double or TypeCode.Single => true,
            _ => false
        };
    }

    private static T? ConvertNumericSafely<T>(object? value, Type targetType)
    {
        // Handle special double values that cause overflow
        if (value is double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
                throw new OverflowException($"Cannot convert {d} to {targetType.Name}");
                
            // For integer targets, check if the value is within range and truncate
            if (IsIntegerType(targetType))
            {
                return ConvertDoubleToInteger<T>(d, targetType);
            }
        }
        
        // Handle special float values  
        if (value is float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f))
                throw new OverflowException($"Cannot convert {f} to {targetType.Name}");
                
            // For integer targets, check if the value is within range and truncate
            if (IsIntegerType(targetType))
            {
                return ConvertDoubleToInteger<T>(f, targetType);
            }
        }
        
        // Use Convert.ChangeType for other numeric conversions
        return (T?)Convert.ChangeType(value, targetType);
    }

    private static bool IsIntegerType(Type type)
    {
        return Type.GetTypeCode(type) switch
        {
            TypeCode.Byte or TypeCode.SByte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64 or 
            TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 => true,
            _ => false
        };
    }

    private static T ConvertDoubleToInteger<T>(double value, Type targetType)
    {
        // Check bounds and truncate the value
        return Type.GetTypeCode(targetType) switch
        {
            TypeCode.Int32 => value > int.MaxValue || value < int.MinValue 
                ? throw new OverflowException($"Value {value} is out of range for Int32")
                : (T)(object)(int)Math.Truncate(value),
            TypeCode.Int16 => value > short.MaxValue || value < short.MinValue 
                ? throw new OverflowException($"Value {value} is out of range for Int16")
                : (T)(object)(short)Math.Truncate(value),
            TypeCode.Int64 => value > long.MaxValue || value < long.MinValue 
                ? throw new OverflowException($"Value {value} is out of range for Int64")
                : (T)(object)(long)Math.Truncate(value),
            TypeCode.Byte => value > byte.MaxValue || value < byte.MinValue 
                ? throw new OverflowException($"Value {value} is out of range for Byte")
                : (T)(object)(byte)Math.Truncate(value),
            TypeCode.SByte => value > sbyte.MaxValue || value < sbyte.MinValue 
                ? throw new OverflowException($"Value {value} is out of range for SByte")
                : (T)(object)(sbyte)Math.Truncate(value),
            TypeCode.UInt16 => value > ushort.MaxValue || value < ushort.MinValue 
                ? throw new OverflowException($"Value {value} is out of range for UInt16")
                : (T)(object)(ushort)Math.Truncate(value),
            TypeCode.UInt32 => value > uint.MaxValue || value < uint.MinValue 
                ? throw new OverflowException($"Value {value} is out of range for UInt32")
                : (T)(object)(uint)Math.Truncate(value),
            TypeCode.UInt64 => value > ulong.MaxValue || value < 0 
                ? throw new OverflowException($"Value {value} is out of range for UInt64")
                : (T)(object)(ulong)Math.Truncate(value),
            _ => throw new InvalidOperationException($"Unsupported integer type: {targetType.Name}")
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
            TypeCode.DateTime => (T)(object)DateTime.Parse(s),
            _ => throw new InvalidOperationException($"Cannot convert {s} to {typeof(T)}")
        };

    }

    private static T? ConvertJson<T>(this IMessageHub hub, JsonElement? value, Func<object?, T?, T?>? conversion, T? defaultValue = default(T))
    {
        if (value == null)
            return default;
        if (conversion != null)
            return conversion(JsonSerializer.Deserialize<object>(value.Value.GetRawText(), hub.JsonSerializerOptions), defaultValue);
        return JsonSerializer.Deserialize<T>(value.Value.GetRawText(), hub.JsonSerializerOptions);
    }
    private static T? ConvertJson<T>(this IMessageHub hub, JsonObject? value, Func<object?, T?, T?>? conversion,
        T? defaultValue)
    {
        if (value == null)
            return default;
        if (conversion != null)
            return conversion(value.Deserialize<object>(hub.JsonSerializerOptions), defaultValue);
        return value.Deserialize<T>(hub.JsonSerializerOptions);
    }
    public static async Task<ActivityLog> SubmitModel(this ISynchronizationStream stream, ModelParameter<JsonElement> data)
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

