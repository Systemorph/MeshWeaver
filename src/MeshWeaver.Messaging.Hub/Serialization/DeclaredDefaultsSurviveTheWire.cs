using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging.Serialization;

/// <summary>
/// Keeps an explicitly stored value on the wire when the hub's
/// <see cref="JsonIgnoreCondition.WhenWritingDefault"/> policy would drop it and the READER would
/// re-materialise a DIFFERENT value — the member's declared default.
///
/// <para><b>The defect class.</b> <c>WhenWritingDefault</c> compares against the CLR default
/// (<c>false</c>, <c>0</c>, <c>null</c>), while a reader re-applies the member's DECLARED default — its
/// initializer (<c>public bool Enabled { get; init; } = true</c>), its constructor parameter's default
/// (<c>record R(bool Enabled = true)</c>), or an initializer computed FROM the constructor's arguments
/// (<c>MeshNode.MainNode = … Id …</c>). Where the two differ, the CLR-default value is omitted on
/// write and silently comes back as the declared one: an off-switch can be turned on but never off.
/// Measured on a live portal: an <c>ActionHandlerRule</c> committed with <c>"enabled": false</c> read
/// <c>enabled: true</c> after its sync import, re-arming a rule that ships disabled.</para>
///
/// <para><b>The rule: omit a CLR-default value only when the reader would rebuild that same value.</b>
/// For a settable scalar member (primitive, enum, <see cref="decimal"/>, their
/// <see cref="Nullable{T}"/>, or <see cref="string"/>):</para>
/// <list type="bullet">
/// <item>bound to a constructor parameter — the reader passes the parameter's default, so the member
/// is omittable exactly when that default is the CLR default;</item>
/// <item>on a type built by a parameterless constructor — one probe instance is what the reader
/// starts from;</item>
/// <item>on a type built by a parameterized constructor — no probe can prove the initializer ignores
/// the arguments, so the check is made PER INSTANCE at write time: the instance's own constructor
/// arguments (read from its parameter-bound members, which is what the reader will pass) rebuild a
/// fresh instance, and the CLR-default value is omitted only when the fresh instance reads it too.</item>
/// </list>
/// <para>Anything that cannot be established — a probe, rebuild or getter that throws, a constructor
/// parameter with no serialized member — means the member is written. An explicit CLR default reads
/// back identically, so that branch costs bytes, never a value. A member carrying its own
/// <see cref="JsonIgnoreAttribute"/> keeps its author's condition. Never throws from the resolver.</para>
/// </summary>
internal static class DeclaredDefaultsSurviveTheWire
{
    /// <summary>Applies the rule to <paramref name="typeInfo"/> while the resolver still configures it.</summary>
    /// <param name="typeInfo">An object type's metadata, not yet locked.</param>
    /// <param name="logger">Receives the reason a probe failed (members then fall to "always written"), or null.</param>
    public static void Apply(JsonTypeInfo typeInfo, ILogger? logger)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || typeInfo.Type.IsAbstract || typeInfo.Type.IsInterface)
            return;

        List<JsonPropertyInfo>? candidates = null;
        foreach (var property in typeInfo.Properties)
        {
            if (property.Get is null
                || property.ShouldSerialize is not null
                || (property.Set is null && property.AssociatedParameter is null)
                || !IsScalar(property.PropertyType)
                || property.AttributeProvider?.IsDefined(typeof(JsonIgnoreAttribute), inherit: true) == true)
                continue;
            (candidates ??= []).Add(property);
        }
        if (candidates is null)
            return;

        object? probe = null;
        var probed = false;
        Rebuilder? rebuilder = null;
        var rebuilderBuilt = false;
        List<(JsonPropertyInfo Property, object? ClrDefault)>? rebuilt = null;
        foreach (var property in candidates)
        {
            var clrDefault = ClrDefault(property.PropertyType);
            if (property.AssociatedParameter is { } parameter)
            {
                var declared = parameter.HasDefaultValue
                    ? Coerce(parameter.DefaultValue, property.PropertyType)
                    : clrDefault;
                if (!Equals(declared, clrDefault))
                    property.ShouldSerialize = static (_, _) => true;
                continue;
            }

            if (typeInfo.CreateObject is not null)
            {
                if (!probed)
                {
                    probe = Probe(typeInfo, logger);
                    probed = true;
                }
                if (probe is null || !TryRead(property, probe, typeInfo.Type, logger, out var value) || !Equals(value, clrDefault))
                    property.ShouldSerialize = static (_, _) => true;
                continue;
            }

            if (!rebuilderBuilt)
            {
                rebuilder = Rebuilder.For(typeInfo, logger);
                rebuilderBuilt = true;
            }
            if (rebuilder is null)
            {
                property.ShouldSerialize = static (_, _) => true;
                continue;
            }
            (rebuilt ??= []).Add((property, clrDefault));
        }

        if (rebuilder is null || rebuilt is null)
            return;
        var check = new PerWriteCheck(rebuilder,
            rebuilt.Select(r => r.Property.Get!).ToArray(),
            rebuilt.Select(r => r.ClrDefault).ToArray());
        for (var i = 0; i < rebuilt.Count; i++)
        {
            var index = i;
            var clrDefault = rebuilt[i].ClrDefault;
            rebuilt[i].Property.ShouldSerialize = (instance, value) =>
                !check.Omittable(instance, index, Equals(value, clrDefault));
        }
    }

    private static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(decimal) || underlying == typeof(string);
    }

    private static object? ClrDefault(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null ? Activator.CreateInstance(type) : null;

    private static bool TryRead(JsonPropertyInfo property, object instance, Type type, ILogger? logger, out object? value)
    {
        try
        {
            value = property.Get!(instance);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex,
                "Reading {Type}.{Member} on a probe instance threw; the member is always written",
                type.FullName, property.Name);
            value = null;
            return false;
        }
    }

    /// <summary>The instance a parameterless deserialization starts from, or null when building it threw.</summary>
    private static object? Probe(JsonTypeInfo typeInfo, ILogger? logger)
    {
        try
        {
            return typeInfo.CreateObject!();
        }
        catch (Exception ex)
        {
            // Not swallowed: a failed probe means "not shown", and every probed member is then
            // always written — the conservative answer. Logged so the type can be found.
            logger?.LogDebug(ex,
                "Could not probe the declared defaults of {Type}; its initialized scalar members are always written",
                typeInfo.Type.FullName);
            return null;
        }
    }

    /// <summary>A parameter's default as the member's own type: an enum from its underlying
    /// constant, and <c>= default</c> of a value type (reported as null) as its CLR default.</summary>
    private static object? Coerce(object? value, Type type)
    {
        if (value is null or DBNull)
            return ClrDefault(type);
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsEnum ? Enum.ToObject(underlying, value) : value;
    }

    /// <summary>
    /// Rebuilds an instance the way the reader will: the deserialization constructor called with the
    /// instance's own parameter-bound member values. Stateless: it holds no instance, so a check
    /// always reads the current inputs and nothing outlives the write.
    /// </summary>
    private sealed class Rebuilder(ConstructorInfo constructor, Func<object, object?>[] arguments, Type type, ILogger? logger)
    {
        public Type Type => type;
        public ILogger? Logger => logger;

        public static Rebuilder? For(JsonTypeInfo typeInfo, ILogger? logger)
        {
            if (typeInfo.ConstructorAttributeProvider is not ConstructorInfo constructor)
                return null;
            var parameters = constructor.GetParameters();
            var arguments = new Func<object, object?>[parameters.Length];
            foreach (var property in typeInfo.Properties)
                if (property.AssociatedParameter is { } parameter && property.Get is { } get
                    && parameter.Position >= 0 && parameter.Position < arguments.Length)
                    arguments[parameter.Position] = get;
            for (var i = 0; i < arguments.Length; i++)
            {
                if (arguments[i] is not null)
                    continue;
                logger?.LogDebug(
                    "Constructor parameter {Parameter} of {Type} has no serialized member; its initialized scalar members are always written",
                    parameters[i].Name, typeInfo.Type.FullName);
                return null;
            }
            return new Rebuilder(constructor, arguments, typeInfo.Type, logger);
        }

        /// <summary>
        /// The instance the reader would build from <paramref name="instance"/>'s CURRENT constructor
        /// arguments, or null when building it threw. Never cached here: a mutable instance can change
        /// between writes, and a field on this metadata-owned object would keep the last payload
        /// graph reachable after serialization ends.
        /// </summary>
        public object? Rebuild(object instance)
        {
            try
            {
                var args = new object?[arguments.Length];
                for (var i = 0; i < args.Length; i++)
                    args[i] = arguments[i](instance);
                return constructor.Invoke(args);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Rebuilding {Type} from its constructor arguments threw; its initialized scalar members are written", type.FullName);
                return null;
            }
        }
    }

    /// <summary>
    /// The per-instance verdicts of ONE write: which rebuilt members of a constructor-built type the
    /// reader would rebuild to their CLR default. One rebuild answers every member of the write — a
    /// sparse <c>MeshNode</c> with several unset members costs one constructor call, not one per
    /// member — and nothing outlives the write.
    ///
    /// <para><b>Write scope without write state.</b> System.Text.Json offers no per-write context, so
    /// the scope is keyed by the instance being written, weakly (<see cref="ConditionalWeakTable{TKey,TValue}"/>
    /// on this instance, never static): the table never keeps an instance alive, and its value holds
    /// verdicts only, never the rebuilt object or the payload. Every member's check marks itself
    /// visited; a member finding itself ALREADY visited is the start of the next write, which
    /// rebuilds from that write's current inputs (a mutated instance is never judged against an
    /// earlier write); the entry is dropped once every member was visited. A write that aborts
    /// mid-object leaves a prefix of the members visited — always including the first in write order —
    /// so the next write still starts fresh.</para>
    /// </summary>
    private sealed class PerWriteCheck(Rebuilder rebuilder, Func<object, object?>[] getters, object?[] clrDefaults)
    {
        private readonly ConditionalWeakTable<object, Scope> writes = new();

        /// <summary>Whether the member at <paramref name="index"/> may be omitted from this write of <paramref name="instance"/>.</summary>
        public bool Omittable(object instance, int index, bool isClrDefault)
        {
            Scope scope;
            lock (writes)
            {
                if (!writes.TryGetValue(instance, out scope!) || scope.Visited[index])
                {
                    scope = new Scope(getters.Length);
                    writes.AddOrUpdate(instance, scope);
                }
                scope.Visited[index] = true;
                if (++scope.VisitedCount == getters.Length)
                    writes.Remove(instance);
            }
            if (!isClrDefault)
                return false;
            // Outside the lock: two threads writing the SAME instance at once may both judge it, from
            // the same inputs, so either verdict array is the right one.
            var verdicts = scope.Verdicts ??= Judge(instance);
            return verdicts[index];
        }

        private bool[] Judge(object instance)
        {
            var verdicts = new bool[getters.Length];
            var rebuilt = rebuilder.Rebuild(instance);
            if (rebuilt is null)
                return verdicts;
            for (var i = 0; i < getters.Length; i++)
            {
                try
                {
                    verdicts[i] = Equals(getters[i](rebuilt), clrDefaults[i]);
                }
                catch (Exception ex)
                {
                    rebuilder.Logger?.LogDebug(ex, "Reading a member of a rebuilt {Type} threw; the member is written", rebuilder.Type.FullName);
                }
            }
            return verdicts;
        }

        private sealed class Scope(int members)
        {
            public readonly bool[] Visited = new bool[members];
            public int VisitedCount;
            public bool[]? Verdicts;
        }
    }
}
