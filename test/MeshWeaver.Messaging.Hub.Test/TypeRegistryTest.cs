using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

public class TypeRegistryTest(ITestOutputHelper output) : HubTestBase(output)
{
    record SayHelloRequest : IRequest<HelloEvent>;

    record HelloEvent;

    private record GenericRequest<T>(T Value);

    // A type deliberately NOT registered on any hub — used to prove the resolver warns when a hub
    // serializes a type its TypeRegistry doesn't know (the "serialized from the wrong place" defect).
    private record UnregisteredPayload(string Value);

    // A stand-in for a security content type, used to pin the legacy full-name-alias ordering contract
    // (register full name FIRST, short name LAST) that WithGraphTypes / AddAITypes / AddMeshTypes rely on.
    private record AliasedContent(bool PublicRead);

    protected override MessageHubConfiguration ConfigureHost(
        MessageHubConfiguration configuration
    ) => configuration.WithTypes(typeof(GenericRequest<>), typeof(List<>));

    [Fact]
    public void GenericTypes()
    {
        var host = GetHost();
        var typeRegistry = host.ServiceProvider.GetRequiredService<ITypeRegistry>();
        var canMap = typeRegistry.TryGetCollectionName(typeof(GenericRequest<int>), out var typeName);
        canMap.Should().BeTrue();
        // Short-name default: the outer generic type uses its short name `GenericRequest`1`, not the
        // namespace-qualified full name. Round-trips back to the same type.
        typeName.Should().Be("GenericRequest`1[Int32]");

        canMap = typeRegistry.TryGetType(typeName!, out var mappedType);
        canMap.Should().BeTrue();
        mappedType!.Type.Should().Be(typeof(GenericRequest<int>));
    }

    [Fact]
    public void GenericTypesWithNullableArguments()
    {
        var host = GetHost();
        var typeRegistry = host.ServiceProvider.GetRequiredService<ITypeRegistry>();

        // Test List<int?>
        var canMap = typeRegistry.TryGetCollectionName(typeof(List<int?>), out var typeName);
        canMap.Should().BeTrue();
        typeName.Should().Be("List`1[Int32?]");

        canMap = typeRegistry.TryGetType(typeName!, out var mappedType);
        canMap.Should().BeTrue();
        mappedType!.Type.Should().Be(typeof(List<int?>));

        // Test GenericRequest<int?>
        canMap = typeRegistry.TryGetCollectionName(typeof(GenericRequest<int?>), out typeName);
        canMap.Should().BeTrue();
        typeName.Should().Be("GenericRequest`1[Int32?]");

        canMap = typeRegistry.TryGetType(typeName!, out mappedType);
        canMap.Should().BeTrue();
        mappedType!.Type.Should().Be(typeof(GenericRequest<int?>));
    }

    /// <summary>
    /// Serializing a type that is NOT in the serializing hub's registry stamps a full-name $type and
    /// MUST warn — that warning is how we find a node "serialized from the wrong place" (the
    /// _Provider/_Policy storm). The warning names the type and the publishing hub, and is deduped.
    /// </summary>
    [Fact]
    public void SerializingUnregisteredType_WarnsAndNamesThePublisher()
    {
        var host = GetHost();
        var typeRegistry = host.ServiceProvider.GetRequiredService<ITypeRegistry>();
        // Sanity: the payload type is genuinely unregistered on this hub.
        typeRegistry.TryGetCollectionName(typeof(UnregisteredPayload), out _).Should().BeFalse();

        var captured = new ConcurrentQueue<string>();
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ObjectPolymorphicConverter(typeRegistry));
        options.TypeInfoResolver = new PolymorphicTypeInfoResolver(
            typeRegistry, owner: "mesh/the-publishing-hub", logger: new CapturingLogger(captured));

        // Serialize through the open `object` door so the polymorphic path (and the resolver) runs.
        var json = JsonSerializer.Serialize((object)new UnregisteredPayload("hi"), typeof(object), options);

        // Short-name default (the CURE): an unregistered type is now written with its SHORT-name $type
        // ("UnregisteredPayload"), which a reading hub that registered it under its short name RESOLVES —
        // instead of the namespace-qualified full name that used to read back as an untyped JsonElement.
        json.Should().Contain($"\"{nameof(UnregisteredPayload)}\"",
            "the short-name default writes the $type as the bare type name, which reading hubs resolve");
        json.Should().NotContain(typeof(UnregisteredPayload).FullName!,
            "the namespace-qualified full name must no longer be emitted as the $type discriminator");
        // ...and the resolver STILL surfaces exactly one warning naming the type + the publishing hub, so
        // an unregistered type is still flagged for explicit registration (collision-safety + clarity).
        var warnings = captured.Where(m =>
            m.Contains("Unregistered type") &&
            m.Contains(typeof(UnregisteredPayload).FullName!) &&
            m.Contains("mesh/the-publishing-hub")).ToList();
        warnings.Should().ContainSingle("the unregistered-type warning fires once per type and identifies the hub");
    }

    /// <summary>
    /// The legacy full-name alias contract: registering the full name FIRST and the short
    /// <c>nameof</c> LAST makes BOTH discriminators resolve on read while the hub keeps WRITING the
    /// short name (nameByType is last-write-wins). This is exactly what WithGraphTypes / AddAITypes /
    /// AddMeshTypes now do for the security content types so legacy full-name nodes deserialize.
    /// </summary>
    [Fact]
    public void FullNameAlias_ResolvesBothDiscriminators_ButWritesShortName()
    {
        var host = GetHost();
        var typeRegistry = host.ServiceProvider.GetRequiredService<ITypeRegistry>();

        typeRegistry.WithType(typeof(AliasedContent), typeof(AliasedContent).FullName!); // full name FIRST
        typeRegistry.WithType(typeof(AliasedContent), nameof(AliasedContent));           // short name LAST

        // Read: a legacy full-name $type AND the short name both resolve to the same type.
        typeRegistry.TryGetType(typeof(AliasedContent).FullName!, out var byFull).Should().BeTrue();
        byFull!.Type.Should().Be(typeof(AliasedContent));
        typeRegistry.TryGetType(nameof(AliasedContent), out var byShort).Should().BeTrue();
        byShort!.Type.Should().Be(typeof(AliasedContent));

        // Write: the hub emits the SHORT name (so it never re-introduces full-name nodes).
        typeRegistry.TryGetCollectionName(typeof(AliasedContent), out var writeName).Should().BeTrue();
        writeName.Should().Be(nameof(AliasedContent));
    }

    /// <summary>
    /// Serializing a type from a COLLECTIBLE assembly (a dynamic node / kernel-script compilation —
    /// per-compile CLR identity) must NOT adopt the type into the hub's TypeRegistry. Pre-fix, the
    /// resolver's auto-registration poisoned the shared hub for the pod's lifetime: every later $type
    /// resolution (e.g. mesh-query row deserialization) yielded THAT foreign CLR type, and consumers
    /// holding their OWN compilation of the class read Content as null — the atioz "BalanceSheet
    /// dashboards render empty" outage (agentic-pensions#12). The wire shape must be unchanged (short-name
    /// $type still written); reads on this hub degrade politely to JsonElement, which the consumer recovers.
    /// </summary>
    [Fact]
    public void SerializingCollectibleType_DoesNotPoisonRegistry_AndDegradesReadsToJsonElement()
    {
        var host = GetHost();
        var typeRegistry = host.ServiceProvider.GetRequiredService<ITypeRegistry>();
        var collectibleType = EmitCollectibleType("SharedEntry", "Position");
        collectibleType.Assembly.IsCollectible.Should().BeTrue(
            "the emitted assembly must model a dynamic node compilation (loaded collectible)");

        var captured = new ConcurrentQueue<string>();
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ObjectPolymorphicConverter(typeRegistry));
        options.TypeInfoResolver = new PolymorphicTypeInfoResolver(
            typeRegistry, owner: "mesh/shared-hub", logger: new CapturingLogger(captured));

        var instance = Activator.CreateInstance(collectibleType)!;
        collectibleType.GetProperty("Position")!.SetValue(instance, "Assets");
        var json = JsonSerializer.Serialize(instance, typeof(object), options);

        // Wire shape unchanged: the short-name $type is still written, so hubs that DO own the type
        // (explicit WithType/WithContentType registration) resolve it exactly as before.
        json.Should().Contain("\"SharedEntry\"",
            "serialization must still stamp the short-name $type discriminator");
        json.Should().Contain("Assets", "the payload properties must serialize normally");

        // The poisoning is gone: the registry must NOT have adopted the per-compile CLR identity.
        typeRegistry.TryGetType("SharedEntry", out _).Should().BeFalse(
            "a collectible-assembly type must never be auto-registered by serialization — it would make "
            + "every later $type resolution on this hub yield the foreign/stale CLR type");
        typeRegistry.TryGetCollectionName(collectibleType, out _).Should().BeFalse();

        // Reads on THIS hub degrade politely to JsonElement (recoverable via ContentAs<T> at the
        // consumer) instead of resolving to the foreign type.
        var back = JsonSerializer.Deserialize<object>(json, options);
        back.Should().BeOfType<JsonElement>();
    }

    /// <summary>
    /// Emits a minimal public class with one string property into a COLLECTIBLE dynamic assembly —
    /// the CLR shape of a compiled dynamic-node type (see CompilationCacheService: collectible ALC,
    /// "DynamicNode_*" naming) without dragging Roslyn into this test project.
    /// </summary>
    private static Type EmitCollectibleType(string typeName, string propertyName)
    {
        var assemblyBuilder = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new System.Reflection.AssemblyName("DynamicNode_CollectibleTest"),
            System.Reflection.Emit.AssemblyBuilderAccess.RunAndCollect);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule("main");
        var typeBuilder = moduleBuilder.DefineType(
            typeName, System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Class);
        var field = typeBuilder.DefineField(
            $"_{propertyName}", typeof(string), System.Reflection.FieldAttributes.Private);
        var property = typeBuilder.DefineProperty(
            propertyName, System.Reflection.PropertyAttributes.None, typeof(string), null);
        const System.Reflection.MethodAttributes accessorAttributes =
            System.Reflection.MethodAttributes.Public
            | System.Reflection.MethodAttributes.SpecialName
            | System.Reflection.MethodAttributes.HideBySig;
        var getter = typeBuilder.DefineMethod(
            $"get_{propertyName}", accessorAttributes, typeof(string), Type.EmptyTypes);
        var getterIl = getter.GetILGenerator();
        getterIl.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        getterIl.Emit(System.Reflection.Emit.OpCodes.Ldfld, field);
        getterIl.Emit(System.Reflection.Emit.OpCodes.Ret);
        var setter = typeBuilder.DefineMethod(
            $"set_{propertyName}", accessorAttributes, null, new[] { typeof(string) });
        var setterIl = setter.GetILGenerator();
        setterIl.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        setterIl.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
        setterIl.Emit(System.Reflection.Emit.OpCodes.Stfld, field);
        setterIl.Emit(System.Reflection.Emit.OpCodes.Ret);
        property.SetGetMethod(getter);
        property.SetSetMethod(setter);
        return typeBuilder.CreateType()!;
    }

    /// <summary>
    /// 🚨 A registry entry from a COLLECTIBLE assembly must not survive that assembly's load context.
    ///
    /// <para>Dynamically compiled nodes live in collectible <c>AssemblyLoadContext</c>s and every
    /// recompile unloads the previous one. A registry that keeps the old <see cref="Type"/> both roots
    /// the context (it can never be collected) and leaves a landmine: anything that walks the registry
    /// afterwards dereferences freed metadata. <c>PolymorphicTypeInfoResolver.ComputeDerivedTypes</c>
    /// does exactly that — it enumerates <c>Types</c> and calls <c>IsAssignableFrom</c> on each entry —
    /// and a CI core dump caught it faulting there with FutuRe's node types at recompile v5/v8/v15
    /// (<c>exit=139</c>, AccessViolation, no failing test to point at).</para>
    ///
    /// <para>Asserted as the INVARIANT rather than by provoking the crash: an access violation takes
    /// the whole test host down, so "no stale entry survives unload" is what a test can pin
    /// deterministically — and it is the property the fix has to hold.</para>
    /// </summary>
    [Fact]
    public void CollectibleType_IsEvictedWhenItsLoadContextUnloads()
    {
        var typeRegistry = GetHost().ServiceProvider.GetRequiredService<ITypeRegistry>();

        // A real collectible context holding a real assembly: load THIS assembly again into its own
        // context, so the type has a genuinely distinct CLR identity — the recompile shape exactly.
        var context = new AssemblyLoadContext("evict-test", isCollectible: true);
        var reloaded = context.LoadFromAssemblyPath(typeof(HelloEvent).Assembly.Location);
        var collectibleType = reloaded.GetType(typeof(HelloEvent).FullName!)!;
        collectibleType.Assembly.IsCollectible.Should().BeTrue("the fixture must reproduce the recompile shape");
        collectibleType.Should().NotBeSameAs(typeof(HelloEvent), "a reload is a new CLR identity");

        typeRegistry.WithType(collectibleType, "EvictMe");
        typeRegistry.TryGetType("EvictMe", out var registered).Should().BeTrue();
        registered!.Type.Should().BeSameAs(collectibleType);

        context.Unload();

        // The registry must have dropped it — by name, by type, and (the one the crash walked) from
        // the enumeration the polymorphic resolver iterates.
        typeRegistry.TryGetType("EvictMe", out _).Should()
            .BeFalse("an entry from an unloaded context is freed metadata, not a resolvable type");
        typeRegistry.Types.Should().NotContain(t => t.Key == "EvictMe",
            "ComputeDerivedTypes walks this enumeration and calls IsAssignableFrom on every entry");
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => sink.Enqueue(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
