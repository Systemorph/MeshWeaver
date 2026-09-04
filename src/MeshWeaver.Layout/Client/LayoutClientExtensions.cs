using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Json;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Icon = MeshWeaver.Domain.Icon;

namespace MeshWeaver.Layout.Client;

/// <summary>
/// Extension methods for layout-client data binding and model synchronization.
/// Provides helpers for writing values back to a synchronization stream via JSON Patch,
/// reading data-bound values (synchronous and reactive), and converting raw objects to
/// typed values for the Blazor rendering pipeline.
/// </summary>
public static class LayoutClientExtensions
{
    /// <summary>
    /// Writes <paramref name="value"/> to the location in <paramref name="stream"/> identified by
    /// <paramref name="reference"/>. When <paramref name="model"/> is provided the patch is
    /// applied to the model parameter directly; otherwise it is applied to the stream's current
    /// state and propagated as an update.
    /// </summary>
    /// <param name="stream">The synchronization stream to update.</param>
    /// <param name="value">The new value to write at the pointer location.</param>
    /// <param name="dataContext">The JSON Pointer prefix for relative pointer resolution.</param>
    /// <param name="reference">Identifies the JSON Pointer location to update; no-op when <c>null</c>.</param>
    /// <param name="model">Optional model parameter to update directly instead of the stream.</param>
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

                    return stream.ToChangeItem(ci, updated, patch!, stream.ClientId);
                },
                    ex =>
                    {
                        stream.Hub.ServiceProvider.GetRequiredService<ILoggerFactory>()
                            .CreateLogger(typeof(LayoutClientExtensions)).LogWarning(ex, "Cannot update layout");
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

    /// <summary>
    /// Returns an observable that emits the value at <paramref name="reference"/> within
    /// <paramref name="stream"/> each time it changes, converted to <typeparamref name="T"/>.
    /// Distinct-until-changed; null emissions are filtered out.
    /// </summary>
    /// <typeparam name="T">The target value type to deserialize to.</typeparam>
    /// <param name="stream">The synchronization stream to bind against.</param>
    /// <param name="reference">JSON Pointer reference identifying the value's location.</param>
    /// <param name="dataContext">Optional prefix for relative pointer resolution.</param>
    /// <param name="conversion">Optional custom conversion function applied after deserialization.</param>
    /// <param name="defaultValue">Default value used when the conversion returns <c>null</c>.</param>
    /// <returns>An observable sequence of <typeparamref name="T"/> values as the stream changes.</returns>
    public static IObservable<T> DataBind<T>(this ISynchronizationStream<JsonElement> stream,
        JsonPointerReference reference,
        string? dataContext = null,
        Func<object?, T?, T?>? conversion = null,
        T? defaultValue = default(T)) =>
        stream.GetStream<object>(JsonPointer.Parse(GetPointer(reference.Pointer, dataContext ?? "")))
            .Select(x =>
                conversion is not null
                    ? conversion.Invoke(x, defaultValue)
                    : stream.Hub.ConvertSingle(x, null, defaultValue!))
            .Where(x => x is not null)
            .Select(x => (T)x!)
            .DistinctUntilChanged();


    /// <summary>
    /// Evaluates <paramref name="reference"/> against the current element of <paramref name="model"/>.
    /// </summary>
    /// <param name="model">The model parameter whose current element is evaluated.</param>
    /// <param name="reference">JSON Pointer reference identifying the value location.</param>
    /// <returns>The <see cref="JsonElement"/> at the pointer, or <c>null</c> if not present.</returns>
    public static JsonElement? GetValueFromModel(this ModelParameter<JsonElement> model, JsonPointerReference reference)
    {
        var pointer = JsonPointer.Parse($"/{reference.Pointer}");
        return pointer.Evaluate(model.Element);
    }

    /// <summary>
    /// Synchronously reads the value of <paramref name="value"/> from the stream's current state,
    /// converting the result to <typeparamref name="T"/>. When <paramref name="value"/> is a
    /// <c>JsonPointerReference</c> the pointer is resolved against the stream;
    /// otherwise the value is converted directly. Enums are parsed case-insensitively; numeric
    /// conversions are defensive (no render-crashing exceptions).
    /// </summary>
    /// <typeparam name="T">The target type to produce.</typeparam>
    /// <param name="stream">The synchronization stream whose current state is read.</param>
    /// <param name="value">The raw value or pointer reference to resolve.</param>
    /// <param name="dataContext">The JSON Pointer prefix for relative pointer resolution.</param>
    /// <returns>The resolved and converted value, or <c>default</c> on failure.</returns>
    public static T? GetDataBoundValue<T>(this ISynchronizationStream<JsonElement> stream, object? value, string? dataContext)
    {
        if (value is JsonPointerReference reference)
            return reference.Pointer.StartsWith('/')
                ? stream.GetDataBoundValue<T>(reference.Pointer)
                : stream.GetDataBoundValue<T>($"{dataContext}/{reference.Pointer}");

        if (value is string stringValue && typeof(T).IsEnum)
            // Case-INSENSITIVE + non-throwing: a control property bound to an enum may carry a
            // mis-cased (e.g. "center" vs Center) or unknown literal from a node's Source. A bare
            // Enum.Parse is case-sensitive and THROWS on a miss — and this runs inside a Blazor
            // BuildRenderTree (DataGridView.RenderPropertyColumn), so the throw escapes the render,
            // kills the circuit, and hangs the whole page (prod 2026-06-21: HorizontalAlignment
            // "center"). TryParse(ignoreCase) resolves the common mis-cased case and falls back to
            // default for a genuinely unknown literal — never crash a render over one bad value.
            return Enum.TryParse(typeof(T), stringValue, ignoreCase: true, out var parsed)
                ? (T)parsed!
                : default;

        if (value is null)
            return default;

        // Handle nullable value types
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        // 🚨 This runs inside a Blazor BuildRenderTree (DataGridView.RenderPropertyColumn). A
        // Convert.ChangeType / direct unbox cast THROWS on an unexpected runtime type
        // (InvalidCastException / FormatException / OverflowException) — and a throw here escapes
        // the render, tears down the Blazor circuit and BLANKS the page (the same failure class as
        // the case-sensitive Enum.Parse crash fixed in the enum branch above). A bound column value
        // that arrives as a different shape after a parameter switch (year/PK) must NEVER crash the
        // render: convert defensively and fall back to default, logging the offending value at
        // Debug for diagnosis. stream may be null on the pure unit-test path — log null-safely.
        try
        {
            if (typeof(T).IsValueType)
            {
                // For nullable value types, convert to the underlying type first.
                if (targetType != typeof(T))
                    return (T?)Convert.ChangeType(value, targetType);
                // Non-nullable value type: use the value directly when it is already T,
                // otherwise coerce via ChangeType (a bare (T)value unbox throws on a mismatch).
                return value is T typed ? typed : (T?)Convert.ChangeType(value, targetType);
            }
            return (T?)Convert.ChangeType(value, targetType);
        }
        catch (Exception ex)
        {
            stream?.Hub?.ServiceProvider?.GetService<ILoggerFactory>()
                ?.CreateLogger("MeshWeaver.Layout.GetDataBoundValue")
                .LogDebug(ex, "GetDataBoundValue<{Type}> could not convert value '{Value}' — using default", typeof(T).Name, value);
            return default;
        }
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

    /// <summary>
    /// Returns an observable that emits the deserialized value at <paramref name="reference"/>
    /// each time <paramref name="stream"/> emits. Null values are filtered out.
    /// </summary>
    /// <typeparam name="T">The target type to deserialize to.</typeparam>
    /// <param name="stream">The synchronization stream to observe.</param>
    /// <param name="reference">JSON Pointer reference identifying the value location.</param>
    /// <param name="dataContext">Optional prefix for relative pointer resolution.</param>
    /// <returns>An observable sequence of <typeparamref name="T"/> values.</returns>
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
            return string.IsNullOrEmpty(pointer) ? "/" : $"/{pointer}";
        // Handle empty pointer - bind to the dataContext itself without trailing slash
        if (string.IsNullOrEmpty(pointer))
            return dataContext.TrimEnd('/');
        return $"{dataContext}/{pointer.TrimEnd('/')}";
    }

    /// <summary>
    /// Converts a single raw <paramref name="value"/> to <typeparamref name="T"/> using
    /// <paramref name="conversion"/> when provided, or the hub's JSON serializer and built-in
    /// type coercion otherwise. Handles <see cref="System.Text.Json.JsonElement"/>,
    /// <see cref="System.Text.Json.Nodes.JsonObject"/>, strings, and numeric types.
    /// A raw string is read tolerantly — enums case-insensitively, numerics with a CSS unit suffix
    /// stripped and the invariant culture — and an unreadable one yields <paramref name="defaultValue"/>
    /// rather than throwing out of the render.
    /// </summary>
    /// <typeparam name="T">The target type to produce.</typeparam>
    /// <param name="hub">The hub whose JSON serializer options are used for deserialization.</param>
    /// <param name="value">The raw value to convert; may be <c>null</c>.</param>
    /// <param name="conversion">Optional custom converter; when non-null it overrides built-in coercion.</param>
    /// <param name="defaultValue">Returned when <paramref name="value"/> is <c>null</c>.</param>
    /// <returns>The converted value, or <paramref name="defaultValue"/> for <c>null</c> input.</returns>
    public static T? ConvertSingle<T>(this IMessageHub hub, object? value, Func<object?, T?, T?>? conversion, T? defaultValue = default(T))
    {
        conversion ??= null;
        if (conversion != null)
            return conversion.Invoke(value, defaultValue);
        // A node's icon field legitimately holds a RAW STRING — a Fluent icon name ("Document"),
        // an image URL ("/static/NodeTypeIcons/document.svg"), or inline <svg> markup. Binding
        // such a string into an Icon-typed slot goes through the TOTAL Icon.Parse; the generic
        // paths (Deserialize / ConvertString) both throw on the string form, which logged a
        // `fail` line on EVERY NavLink render (prod error storm) while the icon rendered as nothing.
        if (typeof(T) == typeof(Icon) && TryGetStringValue(value, out var iconString))
            return (T?)(object?)Icon.Parse(iconString);
        return value switch
        {
            null => defaultValue,
            // ReSharper disable ExpressionIsAlwaysNull
            JsonElement element => hub.ConvertJson(element, conversion, defaultValue),
            JsonObject obj => hub.ConvertJson(obj, conversion, defaultValue),
            JsonNode node => node.Deserialize<T>(hub.JsonSerializerOptions),
            // ReSharper restore ExpressionIsAlwaysNull
            T t => t,
            string s => hub.ConvertString(s, defaultValue),
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
        return ConvertNumericValue<T>(value);
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
        // Use targetType (underlying type for nullables) since Convert.ChangeType doesn't support nullable types
        var converted = Convert.ChangeType(value, targetType);
        return (T?)converted;
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
        return value is null ? default : (T?)Convert.ChangeType(value, targetType);
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

    /// <summary>
    /// Extracts a plain string from the raw binding value regardless of how it arrived —
    /// as a CLR string, a <see cref="JsonElement"/> string token, or a <see cref="JsonValue"/>
    /// string node — so type-specific string parsing (e.g. <see cref="Icon.Parse"/>) sees ONE shape.
    /// </summary>
    private static bool TryGetStringValue(object? value, out string? s)
    {
        s = value switch
        {
            string str => str,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonValue jv when jv.TryGetValue<string>(out var nodeString) => nodeString,
            _ => null
        };
        return s != null;
    }

    /// <summary>
    /// Parses a raw string binding value into <typeparamref name="T"/>. TOTAL by design: every form the
    /// layout layer legitimately emits (a mis-cased enum literal, a CSS length) converts, and anything
    /// genuinely unreadable degrades to <paramref name="defaultValue"/> instead of throwing.
    /// </summary>
    private static T? ConvertString<T>(this IMessageHub hub, string s, T? defaultValue)
    {
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        // Case-INSENSITIVE, mirroring the enum branch of GetDataBoundValue above — this is the same fix,
        // on the sibling path that was missed. The lowercase form is not a mistake to tolerate, it is the
        // DOCUMENTED one: Stack.md's configuration table lists `"start"`, `"center"`, `"end"` for
        // WithHorizontalAlignment/WithVerticalAlignment while the CLR members are PascalCase, so a bare
        // (case-sensitive) Enum.Parse rejects exactly what the docs tell an author to write — issue #1658,
        // HorizontalAlignment "center" in Area Play/5.
        if (targetType.IsEnum)
            return Enum.TryParse(targetType, s, ignoreCase: true, out var parsedEnum)
                ? (T)parsedEnum!
                : Unreadable(hub, s, defaultValue);

        // The skin properties carrying sizes are `object?`, and every example in the framework's own docs
        // fills them with a CSS length — Stack.md lists `"8px"`, `"1rem"`, `"16px"` for WithVerticalGap /
        // WithHorizontalGap — while the Blazor view binds them into `int?` (LayoutStackView's
        // VerticalGap/HorizontalGap, px-valued for FluentStack). So a bare int.Parse throws on the
        // documented call site — issue #1657, "8px" in Area Play/4. Strip the unit and read the magnitude.
        //
        // No unit CONVERSION happens: there is no root font size and no containing box on this path, so
        // "1.5rem" yields 1.5 rather than a guessed 24. px is the unit every framework example uses and the
        // one these numeric slots are already denominated in; the other units are accepted so a
        // hand-authored node degrades to a plausible number instead of to nothing.
        var number = StripCssUnit(s);   // a span into s — no allocation

        // Parse INVARIANT, never CurrentCulture. These strings arrive off the wire as JSON or CSS, where
        // "1.5" always means one-and-a-half; `double.Parse("1.5")` on a comma-decimal thread reads 15.
        object? converted = Type.GetTypeCode(targetType) switch
        {
            TypeCode.Int32 => TryReadIntegral(number, int.MinValue, int.MaxValue, out var i32) ? (object)(int)i32 : null,
            TypeCode.Int64 => TryReadIntegral(number, long.MinValue, long.MaxValue, out var i64) ? (object)i64 : null,
            TypeCode.Int16 => TryReadIntegral(number, short.MinValue, short.MaxValue, out var i16) ? (object)(short)i16 : null,
            TypeCode.Byte => TryReadIntegral(number, byte.MinValue, byte.MaxValue, out var u8) ? (object)(byte)u8 : null,
            TypeCode.Double => double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl) ? dbl : null,
            TypeCode.Single => float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var flt) ? flt : null,
            TypeCode.Boolean => bool.TryParse(s, out var b) ? b : null,
            TypeCode.Char => s.Length == 1 ? s[0] : null,
            TypeCode.DateTime => DateTime.TryParse(s, out var dt) ? dt : null,
            _ => null
        };

        return converted is null ? Unreadable(hub, s, defaultValue) : (T)converted;
    }

    /// <summary>
    /// Returns <paramref name="s"/> with a trailing CSS unit removed ("8px" → "8", "-4px" → "-4",
    /// "1.5rem" → "1.5", "50%" → "50"), or unchanged when it carries none. Stays a span the whole way —
    /// the number is handed straight to the span TryParse overloads, so the render path allocates nothing.
    /// </summary>
    private static ReadOnlySpan<char> StripCssUnit(ReadOnlySpan<char> s)
    {
        var span = s.Trim();
        var digits = span.Length;
        while (digits > 0 && !char.IsAsciiDigit(span[digits - 1]) && span[digits - 1] != '.')
            digits--;
        // The suffix must be a RECOGNISED unit, and there must be a number in front of it. That is what
        // keeps the tolerance narrow: "8 apples" and "auto" stay unreadable (→ the default) rather than
        // silently becoming 8 and 0.
        return digits > 0 && digits < span.Length && IsCssUnit(span[digits..])
            ? span[..digits]
            : s;
    }

    /// <summary>
    /// The CSS length and percentage units a bound layout value may carry. `px` and `%` are what the
    /// framework's controls and docs actually emit; the rest are the CSS spec's remaining length units,
    /// listed so a hand-authored `"1.5rem"` / `"2em"` / `"50vh"` reads the same way. Units are
    /// case-insensitive in CSS ("8PX" is legal), hence the fold before the match — done in a stack
    /// buffer rather than via ToString(), because this sits on the render path and a per-binding
    /// allocation there is per-frame GC pressure.
    /// </summary>
    private static bool IsCssUnit(ReadOnlySpan<char> unit)
    {
        // Every unit below is at most four characters; the length cap is also what bounds the buffer.
        if (unit.Length is 0 or > MaxCssUnitLength)
            return false;
        Span<char> lower = stackalloc char[MaxCssUnitLength];
        unit.ToLowerInvariant(lower);
        return lower[..unit.Length] switch
        {
            "%" or "px" or "rem" or "em" or "ex" or "ch" or "cap" or "ic" or "lh" or "rlh" => true,
            "vw" or "vh" or "vmin" or "vmax" or "vi" or "vb" => true,
            "cm" or "mm" or "q" or "in" or "pt" or "pc" or "fr" => true,
            _ => false
        };
    }

    private const int MaxCssUnitLength = 4;

    /// <summary>
    /// Reads an integral magnitude, accepting a fractional one by truncating it — the same thing
    /// <see cref="ConvertDoubleToInteger{T}"/> already does for a bound double, so "1.5rem" in an `int`
    /// slot behaves like 1.5 in an `int` slot instead of vanishing. Out of range yields <c>false</c>
    /// (→ the default), never an OverflowException thrown out of a render.
    /// </summary>
    private static bool TryReadIntegral(ReadOnlySpan<char> s, long min, long max, out long value)
    {
        if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            // The exact parse failed, so this is a fractional magnitude ("1.5rem"). Range-check it as a
            // double BEFORE truncating — and cap at 2^53, past which a double no longer represents
            // integers exactly: `(double)long.MaxValue` rounds UP, so a `d > max` test alone lets a value
            // one ulp too large through and the cast then wraps to long.MinValue rather than failing.
            // Nothing that size is a layout length; refusing it is the honest answer.
            const double exactIntegerLimit = 9007199254740992d; // 2^53
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                || double.IsNaN(d) || double.IsInfinity(d)
                || Math.Abs(d) > exactIntegerLimit || d < min || d > max)
            {
                value = 0;
                return false;
            }
            value = (long)Math.Truncate(d);
        }
        return value >= min && value <= max;
    }

    /// <summary>
    /// Reports a string the conversion could not read and yields <paramref name="defaultValue"/>.
    /// </summary>
    /// <remarks>
    /// Degrading rather than throwing is what the neighbouring <c>ConvertJson</c> and
    /// <c>GetDataBoundValue</c> already do, and it is what this path needs: <c>ConvertSingle</c> is called
    /// from <c>DataBind</c>'s <c>Select</c>, so a throw does not just drop one value — it faults the
    /// observable and kills the binding for the lifetime of the view.
    /// Logged at Debug, matching <c>GetDataBoundValue</c>: this runs on the render path, where one bad
    /// literal must not become a per-frame `fail` line (the storm the Icon branch of ConvertSingle was
    /// fixed for). Nothing is hidden that was previously visible — <c>BlazorView.DataBind</c> caught the
    /// throw and applied the default anyway, so the outcome is unchanged and only the noise is gone.
    /// </remarks>
    private static T? Unreadable<T>(IMessageHub hub, string s, T? defaultValue)
    {
        hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Layout.ConvertString")
            .LogDebug("ConvertString<{Type}> could not read '{Value}' — using default", typeof(T).Name, s);
        return defaultValue;
    }

    private static T? ConvertJson<T>(this IMessageHub hub, JsonElement? value, Func<object?, T?, T?>? conversion, T? defaultValue = default(T))
    {
        if (value == null)
            return default;

        // A string-typed control bound to a non-string JSON scalar/collection must DISPLAY it, not crash.
        // The generic read-only property form (EditorExtensions) has no control for an IEnumerable<T> and
        // binds a NUMBER/BOOL scalar into a string-typed LabelControl; `Deserialize<string>("322.844")`
        // (or `"[...]"`/`"{...}"`) throws JsonException — the catch below then returns null, so the field
        // renders BLANK until you click to edit (issue #322; the prod Anthropic `models` array crash).
        // Render the readable text form instead: a scalar array becomes "a, b, c", and a Number/True/False
        // token becomes its own text ("322.844"/"true"/"false"). This is the string-slot analogue of the
        // numeric edit control deserializing the CLR type fine. Only a genuine string target is affected;
        // any other T still deserializes as before and a real failure still falls through to the catch.
        if (conversion == null && typeof(T) == typeof(string)
            && value.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object
                or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            return (T)(object)JsonElementToDisplayString(value.Value);

        try
        {
            if (conversion != null)
                return conversion(JsonSerializer.Deserialize<object>(value.Value.GetRawText(), hub.JsonSerializerOptions), defaultValue);
            return JsonSerializer.Deserialize<T>(value.Value.GetRawText(), hub.JsonSerializerOptions);
        }
        catch (JsonException ex)
        {
            hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Layout.ConvertJson")
                .LogError(ex, "ConvertJson<{Type}> failed for JsonElement {ValueKind}: {Raw}",
                    typeof(T).Name, value.Value.ValueKind, value.Value.GetRawText()[..Math.Min(100, value.Value.GetRawText().Length)]);
            return defaultValue;
        }
    }
    private static T? ConvertJson<T>(this IMessageHub hub, JsonObject? value, Func<object?, T?, T?>? conversion,
        T? defaultValue)
    {
        if (value == null)
            return default;
        try
        {
            if (conversion != null)
                return conversion(value.Deserialize<object>(hub.JsonSerializerOptions), defaultValue);
            return value.Deserialize<T>(hub.JsonSerializerOptions);
        }
        catch (JsonException ex)
        {
            hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("MeshWeaver.Layout.ConvertJson")
                .LogError(ex, "ConvertJson<{Type}> failed for JsonObject", typeof(T).Name);
            return defaultValue;
        }
    }

    /// <summary>
    /// Renders a non-string JSON value as readable text for a string-typed (read-only) control: a scalar
    /// array becomes "a, b, c"; a Number/True/False/Object token becomes its raw JSON text ("322.844",
    /// "true", "false"); anything containing nested objects/arrays falls back to the raw JSON. Lets a
    /// numeric/boolean/collection property that the generic form bound to a Label/TextField DISPLAY
    /// rather than throw a string-conversion JsonException (issue #322; the prod `models` array crash).
    /// </summary>
    private static string JsonElementToDisplayString(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return element.GetRawText();

        var result = string.Empty;
        var first = true;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return element.GetRawText(); // complex items — show raw JSON rather than a lossy join
            if (!first)
                result += ", ";
            result += item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.GetRawText();
            first = false;
        }
        return result;
    }
    /// <summary>
    /// Submits a model parameter through the synchronization stream's owning hub.
    /// Returns <c>IObservable&lt;ActivityLog&gt;</c> — never <c>Task</c> — to keep the
    /// hub round-trip composable end-to-end (see <c>Doc/Architecture/AsynchronousCalls.md</c>).
    /// Subscribe to receive the activity log; do not bridge with <c>.ToTask()</c>.
    /// </summary>
    public static IObservable<ActivityLog> SubmitModel(this ISynchronizationStream stream, ModelParameter<JsonElement> data)
    {
        var delivery = stream.Hub.Post(
            new DataChangeRequest { Updates = [data.Submit()] },
            o => o.WithTarget(stream.Owner));

        if (delivery is null)
        {
            return Observable.Return(new ActivityLog(ActivityCategory.DataUpdate)
            {
                End = DateTime.UtcNow,
                Messages =
                [
                    new LogMessage("Failed to post DataChangeRequest (no route).", LogLevel.Error)
                        .WithKey("activity.dataUpdate.noRoute")
                ]
            });
        }

        return stream.Hub.Observe(delivery)
            .FirstAsync()
            .Select(callbackResponse =>
            {
                if (callbackResponse.Message is not DataChangeResponse responseMsg)
                {
                    return new ActivityLog(ActivityCategory.DataUpdate)
                    {
                        End = DateTime.UtcNow,
                        Messages =
                        [
                            new LogMessage(
                                $"Unexpected response shape '{callbackResponse.Message?.GetType().Name ?? "null"}'.",
                                LogLevel.Error)
                                .WithKey("activity.dataUpdate.unexpectedResponse",
                                    ("shape", callbackResponse.Message?.GetType().Name ?? "null"))
                        ]
                    };
                }

                if (responseMsg.Status == DataChangeStatus.Committed)
                    data.Confirm();

                return responseMsg.Log;
            })
            .Catch<ActivityLog, Exception>(e => Observable.Return(new ActivityLog(ActivityCategory.DataUpdate)
            {
                End = DateTime.UtcNow,
                // Spelled `new LogMessage(...)` rather than target-typed `new(...)` on purpose: the
                // #3236 ratchet counts the spelled form, and a target-typed one is invisible to it —
                // which is how this file's three entries stayed out of the issue's own census.
                // Deliberately UNKEYED: `e.Message` is upstream text no catalog can carry.
                Messages = [new LogMessage(e.Message, LogLevel.Error)]
            }));
    }
}

