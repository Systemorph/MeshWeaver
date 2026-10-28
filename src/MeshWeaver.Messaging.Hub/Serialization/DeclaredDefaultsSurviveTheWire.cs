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
/// member: nothing is cached per instance, and nothing runs during a write.</para>
/// <para><b>Populate.</b> Every proof above is about a reader that CONSTRUCTS the instance. Under
/// <see cref="JsonObjectCreationHandling.Populate"/> the reader instead fills the instance the
/// containing object already holds, so an omitted member keeps whatever that holder initialized
/// (<c>Flags = new() { Enabled = true }</c>), and no metadata of the child can prove otherwise. So:
/// when the options prefer Populate, nothing is proven and every candidate is written; and a type
/// whose property can populate an object (the property's own attribute, or the type's preference)
/// is refused at resolution with an <see cref="InvalidOperationException"/> when that object's
/// metadata would omit any value — the child's verdict is shared by every holder and cannot be made
/// per holder, and a refusal before the first byte is written is the only outcome that loses nothing.
/// A child that omits nothing (every candidate always written, or marked
/// <c>[JsonIgnore(Condition = JsonIgnoreCondition.Never)]</c>) may be populated. The proof itself
/// never throws.</para>
/// <para><b>The factory and the layout.</b> The constructor IL is a proof of the read only when the
/// reader actually runs that constructor and when a store names ONE member. So nothing is proven when
/// a resolver modifier may have replaced <see cref="JsonTypeInfo.CreateObject"/> before this rule ran
/// (the caller says whether its factory is the resolver's own; a replaced factory is never invoked to
/// find out), a proven member is written anyway when the factory differs at write time from the one
/// the proof saw (a modifier that ran AFTER the rule), and a type whose hierarchy has an explicit
/// layout (<c>[FieldOffset]</c>, where one store can initialize an overlapping field) has every
/// candidate written.</para>
/// </summary>
internal static class DeclaredDefaultsSurviveTheWire
{
    /// <summary>Applies the rule to <paramref name="typeInfo"/> while the resolver still configures it.</summary>
    /// <param name="typeInfo">An object type's metadata, not yet locked.</param>
    /// <param name="logger">Receives the reason a proof could not be made (members then fall to "always written"), or null.</param>
    /// <param name="probe">Builds fresh, uncached metadata of another type exactly as the resolver
    /// would before this rule runs; used to refuse a property that populates an object whose values
    /// would be omitted. Null skips that check.</param>
    /// <param name="factoryIsResolvers">True only when no resolver modifier ran before this rule, so a
    /// non-null <see cref="JsonTypeInfo.CreateObject"/> is the resolver's own constructor call. False
    /// makes every candidate written.</param>
    /// <exception cref="InvalidOperationException">A property of <paramref name="typeInfo"/> can
    /// populate an object whose metadata omits values: an omitted value would read back as the
    /// holder's.</exception>
    public static void Apply(JsonTypeInfo typeInfo, ILogger? logger, Func<Type, JsonTypeInfo>? probe = null, bool factoryIsResolvers = true)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return;
        if (probe is not null)
            RefusePopulatingAnOmittingObject(typeInfo, probe, factoryIsResolvers, logger);
        ApplyOmission(typeInfo, factoryIsResolvers, logger);
    }

    /// <summary>The omission rule; true when some member may still be omitted at its CLR default.</summary>
    private static bool ApplyOmission(JsonTypeInfo typeInfo, bool factoryIsResolvers, ILogger? logger)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || typeInfo.Type.IsAbstract || typeInfo.Type.IsInterface)
            return false;

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
            return false;

        // The constructor proof says what the reader holds BEFORE it sets members. It is a proof of
        // the read only when the reader constructs the instance — never when the options make it
        // populate one a holder already has — and when nothing else writes during the read: an
        // OnDeserializing callback runs after construction, and a hand-written setter of any member
        // it sets can store anywhere.
        // It is also a proof only of the constructor the reader RUNS — never of a factory a modifier
        // installed — and only when a store names one member: an explicit layout can overlap fields.
        var stores = typeInfo.Options.PreferredObjectCreationHandling != JsonObjectCreationHandling.Populate
            && typeInfo.OnDeserializing is null
            && factoryIsResolvers
            && !HasExplicitLayout(typeInfo.Type, logger)
            && SettersAreContained(typeInfo, candidates, logger)
            ? ConstructorStores.Read(typeInfo, logger)
            : null;
        // A modifier that runs after this rule can still replace the factory; the proof then no
        // longer describes the reader, so a proven member is written whenever the factory differs.
        var provenFactory = typeInfo.CreateObject;
        var omits = false;
        foreach (var property in candidates)
        {
            if (stores is null || !ProvenClrDefault(property, stores))
            {
                property.ShouldSerialize = static (_, _) => true;
                continue;
            }
            var clrDefault = ClrDefault(property.PropertyType);
            property.ShouldSerialize = (_, value) =>
                !ReferenceEquals(typeInfo.CreateObject, provenFactory) || !Equals(value, clrDefault);
            omits = true;
        }
        return omits;
    }

    private static bool HasExplicitLayout(Type type, ILogger? logger)
    {
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            if (!current.IsExplicitLayout)
                continue;
            logger?.LogDebug(
                "{Type} has an explicit layout (in {Declaring}); fields may overlap, so its scalar members are always written",
                type.FullName, current.FullName);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Refuses a property of <paramref name="holder"/> that can populate an object whose metadata
    /// omits values (or whose runtime type is unknown: abstract or interface): the reader would keep
    /// the holder's initialized value for every omitted member.
    /// </summary>
    private static void RefusePopulatingAnOmittingObject(JsonTypeInfo holder, Func<Type, JsonTypeInfo> probe, bool factoryIsResolvers, ILogger? logger)
    {
        // STJ populates only through a holder it constructs with no constructor parameters.
        if (UsesParameterizedConstructor(holder))
            return;
        foreach (var property in holder.Properties)
        {
            var handling = property.ObjectCreationHandling
                ?? holder.PreferredPropertyObjectCreationHandling
                ?? holder.Options.PreferredObjectCreationHandling;
            if (handling != JsonObjectCreationHandling.Populate
                || property.Get is null
                || (property.PropertyType.IsValueType && property.Set is null)
                || IsScalar(property.PropertyType))
                continue;
            var child = probe(property.PropertyType);
            if (child.Kind != JsonTypeInfoKind.Object)
                continue;
            if (!child.Type.IsAbstract && !child.Type.IsInterface && !ApplyOmission(child, factoryIsResolvers, logger))
                continue;
            throw new InvalidOperationException(
                $"{holder.Type.FullName}.{property.Name} populates an existing {child.Type.FullName}, whose "
                + "metadata omits values at their CLR default; such a value would read back as the one the "
                + "holder initialized. Replace the object instead of populating it, or mark the omitted "
                + "members [JsonIgnore(Condition = JsonIgnoreCondition.Never)].");
        }
    }

    private static bool UsesParameterizedConstructor(JsonTypeInfo typeInfo) =>
        typeInfo.CreateObject is null
        && typeInfo.ConstructorAttributeProvider is ConstructorInfo constructor
        && constructor.GetParameters().Length > 0;

    /// <summary>
    /// True when every hand-written setter the reader may call provably stores only into fields no
    /// candidate lives in. The setter is straight-line IL made of stores, each
    /// <c>ldarg.0; &lt;value&gt;; stfld</c> into a field no candidate lives in, where the value may be
    /// normalized through calls (<c>_skins = value.ToImmutableList()</c>) but never touches the instance:
    /// <c>this</c> is loaded once per store and stays buried under the stack until its <c>stfld</c>
    /// consumes it, so no call ever receives it and nothing can reach a candidate through it. A branch,
    /// a second use of <c>this</c>, an argument written or whose address escapes, or an instruction this
    /// reader cannot account for makes every candidate written — another member's setter could
    /// otherwise set an omitted member behind the proof's back.
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
        // Stack depth counted from the `this` that opens the current store; 0 = between stores.
        var depth = 0;
        var returned = false;
        var position = 0;
        while (position < il.Length)
        {
            var code = ConstructorStores.ReadOpCode(il, ref position);
            var operand = position;
            position += ConstructorStores.OperandSize(code, il, position);
            if (code == OpCodes.Nop)
                continue;
            if (returned)
                return false;
            if (code == OpCodes.Ret)
            {
                if (depth != 0)
                    return false;
                returned = true;
                continue;
            }
            if (IsLoadThis(code, il, operand))
            {
                if (depth != 0)
                    return false;
                depth = 1;
                continue;
            }
            // Between stores nothing but the next `this` and the final ret is accounted for.
            if (depth == 0
                || (code.FlowControl != FlowControl.Next && code.FlowControl != FlowControl.Call)
                || code == OpCodes.Starg || code == OpCodes.Starg_S || code == OpCodes.Ldarga || code == OpCodes.Ldarga_S
                || code == OpCodes.Calli || code == OpCodes.Ldflda)
                return false;
            if (code == OpCodes.Stfld)
            {
                if (depth != 2)
                    return false;
                var field = setter.Module.ResolveField(BitConverter.ToInt32(il, operand), typeArguments, Type.EmptyTypes);
                if (field is null || backing.Contains((field.Module, field.MetadataToken)))
                    return false;
                depth = 0;
                continue;
            }
            if (StackEffect(code, setter.Module, il, operand, typeArguments) is not var (pops, pushes))
                return false;
            // `this` (depth 1) must never be popped by anything but its own stfld.
            depth -= pops;
            if (depth < 1)
                return false;
            depth += pushes;
        }
        return returned && depth == 0;
    }

    private static bool IsLoadThis(OpCode code, byte[] il, int operand) =>
        code == OpCodes.Ldarg_0
        || (code == OpCodes.Ldarg_S && il[operand] == 0)
        || (code == OpCodes.Ldarg && BitConverter.ToUInt16(il, operand) == 0);

    /// <summary>The values an instruction pops and pushes, or null when this reader cannot tell.</summary>
    private static (int Pops, int Pushes)? StackEffect(OpCode code, Module module, byte[] il, int operand, Type[] typeArguments)
    {
        int pops, pushes;
        if (code.StackBehaviourPop == StackBehaviour.Varpop || code.StackBehaviourPush == StackBehaviour.Varpush)
        {
            if (code != OpCodes.Call && code != OpCodes.Callvirt && code != OpCodes.Newobj)
                return null;
            if (module.ResolveMethod(BitConverter.ToInt32(il, operand), typeArguments, Type.EmptyTypes) is not { } method)
                return null;
            var parameters = method.GetParameters().Length;
            if (code == OpCodes.Newobj)
                return (parameters, 1);
            pops = parameters + (method.IsStatic ? 0 : 1);
            pushes = method is MethodInfo { ReturnType: var returns } && returns != typeof(void) ? 1 : 0;
            return (pops, pushes);
        }
        pops = code.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi
                or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8
                or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
                or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8
                or StackBehaviour.Popref_popi_popref or StackBehaviour.Popref_popi_pop1 => 3,
            _ => -1,
        };
        pushes = code.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4
                or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
            StackBehaviour.Push1_push1 => 2,
            _ => -1,
        };
        return pops < 0 || pushes < 0 ? null : (pops, pushes);
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
