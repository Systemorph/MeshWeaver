using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using MeshWeaver.Domain;
using MeshWeaver.Messaging.Serialization;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The overlay registry (<c>Doc/Architecture/TypeRegistryLayers</c>) must serve, from every layer, the
/// definition THAT layer would serve if it held everything itself: the name a hub renamed a type to,
/// an explicit closed-generic registration, a key a middle layer's factory decides, a nested generic
/// name, and the unload state an ancestor already observed. Each test fails on the overlay as first
/// proposed in #7167 and names the review finding it pins.
/// </summary>
public class TypeRegistryOverlayServesTheEffectiveDefinitionTest
{
    /// <summary>A polymorphic base.</summary>
    public abstract record Shape;

    /// <summary>A derived type a hub renames.</summary>
    public record Circle(double Radius) : Shape;

    /// <summary>A generic derived type registered closed.</summary>
    public record Box<T>(T Content) : Shape;

    /// <summary>A generic type with no supertype a derived-type search could reach.</summary>
    public record Plain<T>(string Id, T Value);

    /// <summary>A type with no conventional key.</summary>
    public record Keyless(string Label);

    private static JsonSerializerOptions OptionsFor(ITypeRegistry registry)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ObjectPolymorphicConverter(registry));
        options.TypeInfoResolver = new PolymorphicTypeInfoResolver(registry);
        return options;
    }

    /// <summary>
    /// A hub that renames an inherited derived type serialises it, through its base, under ITS name —
    /// not the parent's, which the merged enumeration yields first.
    /// </summary>
    [Fact]
    public void ARenamedInheritedSubtype_SerializedThroughItsBase_CarriesTheHubsName()
    {
        var mesh = MessageHubExtensions.CreateTypeRegistry()
            .WithType(typeof(Shape), nameof(Shape))
            .WithType(typeof(Circle), nameof(Circle));
        var hub = MessageHubExtensions.CreateTypeRegistry(mesh).WithType(typeof(Circle), "Round");

        hub.GetCollectionName(typeof(Circle)).Should().Be("Round", "the precondition: the hub's override");
        var json = JsonSerializer.Serialize<Shape>(new Circle(2), OptionsFor(hub));

        json.Should().Contain("\"Round\"", "the hub serialises its renamed type under its own name");
        json.Should().NotContain($"\"{nameof(Circle)}\"", "the parent's name is not this hub's discriminator");
    }

    /// <summary>
    /// A closed generic registered explicitly under the name the chain would synthesise is HELD: it is
    /// enumerated (so it is discovered as a derived type of its base), resolvable by name, and keyable.
    /// </summary>
    [Fact]
    public void AnExplicitClosedGenericRegistration_IsEnumeratedAndResolvableByName()
    {
        var name = $"{nameof(Box<int>)}`1[Int32]";
        var mesh = MessageHubExtensions.CreateTypeRegistry()
            .WithType(typeof(Shape), nameof(Shape))
            .WithType(typeof(Box<>))
            .WithType(typeof(Box<int>), name);

        mesh.Types.Should().Contain(t => t.Value.Type == typeof(Box<int>), "the registration is enumerated");
        mesh.GetTypeDefinition(name)!.Type.Should().Be(typeof(Box<int>), "the registered name resolves");
        mesh.WithKeyFunction(name, new KeyFunction(o => ((Box<int>)o).Content, typeof(int)));
        mesh.GetKeyFunction(name)!.Function(new Box<int>(7)).Should().Be(7, "the registered name is keyable");

        var hub = MessageHubExtensions.CreateTypeRegistry(mesh).WithType(typeof(Box<int>), name);
        hub.OwnTypes.Should().BeEmpty("a descendant re-registering it stores nothing");

        var json = JsonSerializer.Serialize<Shape>(new Box<int>(5), OptionsFor(hub));
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("$type").GetString().Should().Be(name, "the closure is discovered through its base");
        document.RootElement.GetProperty(nameof(Box<int>.Content)).GetInt32().Should().Be(5, "the derived fields are serialised");
    }

    /// <summary>
    /// A closed generic no derived-type search can reach (no supertype but <c>object</c>), registered
    /// under its synthesised name, stores nothing — and still resolves by name, keys by name and takes
    /// a key override by name, through the bracket parse.
    /// </summary>
    [Fact]
    public void AnUnreachableClosedGeneric_StoresNothing_AndResolvesByName()
    {
        var name = $"{nameof(Plain<int>)}`1[Int32]";
        var mesh = MessageHubExtensions.CreateTypeRegistry().WithType(typeof(Plain<>));
        var hub = MessageHubExtensions.CreateTypeRegistry(mesh).WithType(typeof(Plain<int>), name);

        hub.OwnTypes.Should().BeEmpty("the chain synthesises the closure; nothing is held");
        hub.GetCollectionName(typeof(Plain<int>)).Should().Be(name);
        hub.GetTypeDefinition(name)!.Type.Should().Be(typeof(Plain<int>), "the name resolves through the bracket parse");
        hub.GetKeyFunction(name)!.Function(new Plain<int>("p1", 3)).Should().Be("p1", "the conventional key is read by name");

        hub.WithKeyFunction(name, new KeyFunction(o => ((Plain<int>)o).Value, typeof(int)));
        hub.GetKeyFunction(name)!.Function(new Plain<int>("p1", 3)).Should().Be(3, "a key override by name is held");
        hub.GetKeyFunction(typeof(Plain<int>))!.Function(new Plain<int>("p1", 3)).Should().Be(3);
    }

    /// <summary>
    /// A key a MIDDLE layer's factory decides is served at the leaf even when the leaf is asked first,
    /// and it is bound in the middle layer, which owns the factory.
    /// </summary>
    [Fact]
    public void AMiddleLayersKeyFactory_KeysALeafFirstRead()
    {
        var root = MessageHubExtensions.CreateTypeRegistry().WithType(typeof(Keyless), nameof(Keyless));
        var middle = MessageHubExtensions.CreateTypeRegistry(root)
            .WithKeyFunctionProvider(t => t == typeof(Keyless) ? new KeyFunction(o => ((Keyless)o).Label, typeof(string)) : null);
        var leaf = MessageHubExtensions.CreateTypeRegistry(middle);

        leaf.GetKeyFunction(nameof(Keyless))!.Function(new Keyless("first")).Should().Be("first",
            "the leaf is read first and still sees the middle layer's key");
        leaf.GetKeyFunction(typeof(Keyless)).Should().NotBeNull();
        middle.OwnTypes.Select(t => t.Key).Should().Equal([nameof(Keyless)], "the keyed override is the middle layer's");
        leaf.OwnTypes.Should().BeEmpty("the leaf owns no factory and holds nothing");
        root.GetKeyFunction(nameof(Keyless)).Should().BeNull("the root's definition is untouched");
    }

    /// <summary>
    /// A synthesised nested generic name round-trips: its arguments split at bracket depth zero only.
    /// </summary>
    [Fact]
    public void ANestedGenericName_RoundTrips()
    {
        var registry = MessageHubExtensions.CreateTypeRegistry();
        var type = typeof(ValueTuple<int, ValueTuple<int, int>>);

        var name = registry.GetOrAddType(type);

        name.Should().Contain("[Int32,").And.EndWith("]]", "the precondition: a nested generic name");
        registry.GetType(name).Should().Be(type);
    }

    /// <summary>
    /// A child with its own key factory that first resolves a collectible type AFTER its parent saw the
    /// load context unload does not bind it strongly: nothing reachable from the registries keeps the
    /// superseded assembly alive.
    /// </summary>
    [Fact]
    public void ALateKeyedOverrideOfAnUnloadingType_DoesNotRootTheAssembly()
    {
        var parent = MessageHubExtensions.CreateTypeRegistry();
        var child = MessageHubExtensions.CreateTypeRegistry(parent)
            .WithKeyFunctionProvider(t => t.Name == nameof(Keyless) && t.Assembly.IsCollectible
                ? new KeyFunction(_ => "k", typeof(string))
                : null);
        var context = ResolveAfterUnload(parent, child);

        for (var i = 0; i < 20 && context.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        context.IsAlive.Should().BeFalse("the child's late keyed override must not root the unloading assembly");
        GC.KeepAlive(parent);
        GC.KeepAlive(child);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ResolveAfterUnload(ITypeRegistry parent, ITypeRegistry child)
    {
        var context = new AssemblyLoadContext("overlay-late-keyed", isCollectible: true);
        var type = context.LoadFromAssemblyPath(typeof(Keyless).Assembly.Location).GetType(typeof(Keyless).FullName!)!;
        parent.WithType(type, "CollectibleKeyless");
        context.Unload();

        child.GetKeyFunction("CollectibleKeyless").Should().NotBeNull(
            "the precondition: the demoted type still resolves, keyed by the child's factory");
        return new WeakReference(context);
    }
}
