using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
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
/// <para><b>The rule: a CLR-default value is omitted only when the reader PROVABLY rebuilds it.</b>
/// The proof is STATIC and is made once, when the type's metadata is resolved: it reads the IL of the
/// constructor the reader calls, and never constructs, probes or rebuilds anything — at resolution
/// time or at write time. For a settable scalar member (primitive, enum, <see cref="decimal"/>, their
/// <see cref="Nullable{T}"/>, or <see cref="string"/>) that is a compiler-generated auto-property or a
/// field, the CLR default is proven when the reader's constructor chain either</para>
/// <list type="bullet">
/// <item>never stores the member's backing field (no initializer, no constructor assignment), or</item>
/// <item>stores it exactly once, straight from the member's OWN constructor parameter
/// (<c>ldarg.0; ldarg.n; stfld</c> — the positional-record shape) whose default is the CLR default, so
/// an omitted value comes back as that default.</item>
/// </list>
/// <para>The proof also requires that nothing else writes while the reader sets members: a type with
/// an <c>OnDeserializing</c> callback, or with a hand-written setter that does more than store the
/// value into a field no candidate lives in, has every candidate written. A constructor that writes
/// or takes the address of an argument (<c>starg</c>, <c>ldarga</c>) is unprovable.</para>
/// <para>Everything else is ALWAYS written: an initializer of any kind, a value derived from another
/// argument, a store in a base or chained constructor, a constructor body that does anything at all
/// (it could set the member through a call), a hand-written accessor, a value type, a constructor the
/// metadata does not name, or IL that cannot be read. An explicit CLR default reads back identically,
/// so the conservative branch costs bytes, never a value. A member carrying its own
/// <see cref="JsonIgnoreAttribute"/> keeps its author's condition. The verdict is a constant per
/// member: nothing is cached per instance, and nothing runs during a write. Never throws from the
/// resolver.</para>
/// </summary>
internal static class DeclaredDefaultsSurviveTheWire
{
    /// <summary>Applies the rule to <paramref name="typeInfo"/> while the resolver still configures it.</summary>
    /// <param name="typeInfo">An object type's metadata, not yet locked.</param>
    /// <param name="logger">Receives the reason a proof could not be made (members then fall to "always written"), or null.</param>
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

        // The constructor proof says what the reader holds BEFORE it sets members. It is a proof of
        // the read only when nothing else writes during the read: an OnDeserializing callback runs
        // after construction, and a hand-written setter of any member it sets can store anywhere.
        var stores = typeInfo.OnDeserializing is null && SettersAreContained(typeInfo, candidates, logger)
            ? ConstructorStores.Read(typeInfo, logger)
            : null;
        foreach (var property in candidates)
            if (stores is null || !ProvenClrDefault(property, stores))
                property.ShouldSerialize = static (_, _) => true;
    }

    /// <summary>
    /// True when every hand-written setter the reader may call provably stores only into fields no
    /// candidate lives in: its IL is nothing but <c>ldarg.0</c>/<c>ldarg.1</c>/<c>stfld</c>/<c>nop</c>/<c>ret</c>
    /// (no call, no branch, no normalization). Anything else makes every candidate written — another
    /// member's setter could otherwise set an omitted member behind the proof's back.
    /// </summary>
    private static bool SettersAreContained(JsonTypeInfo typeInfo, List<JsonPropertyInfo> candidates, ILogger? logger)
    {
        var backing = candidates.Select(BackingField).OfType<FieldInfo>()
            .Select(f => (f.Module, f.MetadataToken)).ToImmutableHashSet();
        foreach (var property in typeInfo.Properties)
        {
            if (property.Set is null
                || property.AttributeProvider is not PropertyInfo { SetMethod: { } setter } info
                || setter.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                continue;
            bool contained;
            try
            {
                contained = SetterIsContained(setter, backing);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "The setter of {Type}.{Member} could not be read", typeInfo.Type.FullName, info.Name);
                contained = false;
            }
            if (!contained)
            {
                logger?.LogDebug(
                    "{Type}.{Member} has a setter that may store into another member; its scalar members are always written",
                    typeInfo.Type.FullName, info.Name);
                return false;
            }
        }
        return true;
    }

    private static bool SetterIsContained(MethodInfo setter, ImmutableHashSet<(Module, int)> backing)
    {
        var il = setter.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
            return false;
        var declaring = setter.DeclaringType!;
        var typeArguments = declaring.IsGenericType ? declaring.GetGenericArguments() : Type.EmptyTypes;
        var position = 0;
        while (position < il.Length)
        {
            var code = ConstructorStores.ReadOpCode(il, ref position);
            var operand = position;
            position += ConstructorStores.OperandSize(code, il, position);
            if (code == OpCodes.Nop || code == OpCodes.Ldarg_0 || code == OpCodes.Ldarg_1 || code == OpCodes.Ret)
                continue;
            if (code != OpCodes.Stfld)
                return false;
            var field = setter.Module.ResolveField(BitConverter.ToInt32(il, operand), typeArguments, Type.EmptyTypes);
            if (field is null || backing.Contains((field.Module, field.MetadataToken)))
                return false;
        }
        return true;
    }

    private static bool ProvenClrDefault(JsonPropertyInfo property, ConstructorStores stores)
    {
        if (BackingField(property) is not { } field)
            return false;
        var found = stores.Of(field);
        if (found.Count == 0)
            return true;
        if (found.Count != 1 || found[0] is not { } argument
            || property.AssociatedParameter is not { } parameter
            || parameter.Position != argument)
            return false;
        // Stored straight from its own parameter: an omitted value makes the reader pass the
        // parameter's default, which must then be the CLR default.
        var clrDefault = ClrDefault(property.PropertyType);
        var declared = parameter.HasDefaultValue ? Coerce(parameter.DefaultValue, property.PropertyType) : clrDefault;
        return Equals(declared, clrDefault);
    }

    /// <summary>The field the member's value lives in, when the getter provably just returns it.</summary>
    private static FieldInfo? BackingField(JsonPropertyInfo property) => property.AttributeProvider switch
    {
        FieldInfo field => field,
        PropertyInfo { GetMethod: { } getter } info when getter.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
            => info.DeclaringType?.GetField($"<{info.Name}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
        _ => null,
    };

    private static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(decimal) || underlying == typeof(string);
    }

    private static object? ClrDefault(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null ? Activator.CreateInstance(type) : null;

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
    /// Every store to an instance field made by the constructor chain the reader runs, read from IL.
    /// A store is recorded with the parameter position it copies when it is the direct
    /// <c>ldarg.0; ldarg.n; stfld</c> shape in the reader's OWN constructor, and with null otherwise.
    /// </summary>
    private sealed class ConstructorStores
    {
        private ImmutableDictionary<(Module, int), ImmutableList<int?>> stores =
            ImmutableDictionary<(Module, int), ImmutableList<int?>>.Empty;

        public ImmutableList<int?> Of(FieldInfo field) =>
            stores.GetValueOrDefault((field.Module, field.MetadataToken), ImmutableList<int?>.Empty);

        /// <summary>The stores, or null when they cannot be established — then every candidate is written.</summary>
        public static ConstructorStores? Read(JsonTypeInfo typeInfo, ILogger? logger)
        {
            var type = typeInfo.Type;
            if (type.IsValueType || typeInfo.ConstructorAttributeProvider is not ConstructorInfo constructor
                || constructor.DeclaringType != type)
            {
                logger?.LogDebug("No class constructor named for {Type}; its scalar members are always written", type.FullName);
                return null;
            }
            try
            {
                var result = new ConstructorStores();
                return result.Walk(constructor, own: true) ? result : Unreadable(type, logger, null);
            }
            catch (Exception ex)
            {
                return Unreadable(type, logger, ex);
            }
        }

        private static ConstructorStores? Unreadable(Type type, ILogger? logger, Exception? ex)
        {
            logger?.LogDebug(ex,
                "The constructor chain of {Type} could not be proven free of side effects; its scalar members are always written",
                type.FullName);
            return null;
        }

        /// <summary>
        /// Reads one constructor: stores before its base/chained constructor call are initializers,
        /// that call is followed, and anything after it other than <c>nop</c>/<c>ret</c> makes the chain
        /// unprovable (a body can set a member through any call).
        /// </summary>
        private bool Walk(ConstructorInfo constructor, bool own)
        {
            var declaring = constructor.DeclaringType!;
            if (declaring == typeof(object))
                return true;
            var il = constructor.GetMethodBody()?.GetILAsByteArray();
            if (il is null)
                return false;
            var module = constructor.Module;
            var typeArguments = declaring.IsGenericType ? declaring.GetGenericArguments() : Type.EmptyTypes;
            var chained = false;
            OpCode? previous = null, beforePrevious = null;
            int? previousArgument = null;
            var position = 0;
            while (position < il.Length)
            {
                var code = ReadOpCode(il, ref position);
                var operand = position;
                position += OperandSize(code, il, position);

                if (chained)
                {
                    if (code != OpCodes.Nop && code != OpCodes.Ret)
                        return false;
                    continue;
                }

                // An argument written or whose address escapes before a store makes `ldarg.n; stfld`
                // no longer "the parameter's value": the chain is unprovable.
                if (code == OpCodes.Starg || code == OpCodes.Starg_S || code == OpCodes.Ldarga || code == OpCodes.Ldarga_S)
                    return false;

                if (code == OpCodes.Stfld || code == OpCodes.Ldflda)
                {
                    var field = module.ResolveField(BitConverter.ToInt32(il, operand), typeArguments, Type.EmptyTypes);
                    if (field is null)
                        return false;
                    var direct = own && code == OpCodes.Stfld && beforePrevious == OpCodes.Ldarg_0 && previousArgument is > 0
                        ? previousArgument - 1
                        : null;
                    var key = (field.Module, field.MetadataToken);
                    stores = stores.SetItem(key, stores.GetValueOrDefault(key, ImmutableList<int?>.Empty).Add(direct));
                }
                else if (code == OpCodes.Call)
                {
                    if (module.ResolveMethod(BitConverter.ToInt32(il, operand), typeArguments, Type.EmptyTypes)
                            is ConstructorInfo { IsStatic: false } next
                        && (next.DeclaringType == declaring || next.DeclaringType == declaring.BaseType))
                    {
                        // The base (or this(...)) call ends the initializers. Stores in the next
                        // constructor copy ITS parameters, never the reader's, so none is direct.
                        if (!Walk(next, own: false))
                            return false;
                        chained = true;
                    }
                }

                if (code != OpCodes.Nop)
                {
                    beforePrevious = previous;
                    previous = code;
                    previousArgument = ArgumentIndex(code, il, operand);
                }
            }
            // A class constructor always calls a base or chained constructor; not finding it means
            // the IL is not the shape this reader understands.
            return chained;
        }

        private static int? ArgumentIndex(OpCode code, byte[] il, int operand)
        {
            if (code == OpCodes.Ldarg_1) return 1;
            if (code == OpCodes.Ldarg_2) return 2;
            if (code == OpCodes.Ldarg_3) return 3;
            if (code == OpCodes.Ldarg_S) return il[operand];
            if (code == OpCodes.Ldarg) return BitConverter.ToUInt16(il, operand);
            return null;
        }

        internal static OpCode ReadOpCode(byte[] il, ref int position)
        {
            var first = il[position++];
            if (first != 0xFE)
                return OneByte[first];
            return TwoByte[il[position++]];
        }

        internal static int OperandSize(OpCode code, byte[] il, int position) => code.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, position),
            _ => 4,
        };

        // Immutable opcode lookups, built once from System.Reflection.Emit.OpCodes and never written.
        private static readonly FrozenDictionary<byte, OpCode> OneByte = OpCodesWhere(two: false);
        private static readonly FrozenDictionary<byte, OpCode> TwoByte = OpCodesWhere(two: true);

        private static FrozenDictionary<byte, OpCode> OpCodesWhere(bool two) =>
            typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(f => (OpCode)f.GetValue(null)!)
                .Where(c => two ? c.Size == 2 : c.Size == 1)
                .ToFrozenDictionary(c => unchecked((byte)(c.Value & 0xFF)));
    }
}
