using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// A hub's <see cref="ITypeRegistry"/> is a parent-chained OVERLAY (<c>Doc/Architecture/TypeRegistryLayers</c>):
/// it holds only the types the hub adds or overrides, and resolves everything else through its parent,
/// recursively up to the mesh registry. "Only overrides are re-registered": a <c>WithType</c> the chain
/// already answers identically (same type, same name) is a no-op that stores nothing, so a hub that adds
/// nothing holds nothing.
///
/// <para>Measured before the change (#7163, <c>Doc/Architecture/PortalHeapIsHubs</c>): 44.5 KB of type
/// registry per hub, and TWO definitions per type per hub (1,215 <c>PingRequest</c> definitions for 608
/// hubs) — the hub constructor registered <c>PingRequest</c> by name and then again through
/// <c>Register&lt;PingRequest&gt;</c>, and the second call built a new definition while the full-name
/// alias kept the first.</para>
/// </summary>
public class TypeRegistryIsARecursiveOverlayTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const int TypeCount = 24;

    /// <summary>A conventionally keyed type (<c>Id</c>).</summary>
    public record Keyed(string Id, string Label);

    /// <summary>A type with no key at all.</summary>
    public record Keyless(string Label);

    /// <summary>A third type for the three-level chain.</summary>
    public record Leaf(string Id);

    /// <summary>A generic carrier, to obtain many distinct CLR types.</summary>
    public record Carrier<T>(string Id, T Value);

    private static readonly Type[] ManyTypes =
    [
        typeof(Carrier<int>), typeof(Carrier<long>), typeof(Carrier<short>), typeof(Carrier<byte>),
        typeof(Carrier<sbyte>), typeof(Carrier<uint>), typeof(Carrier<ulong>), typeof(Carrier<ushort>),
        typeof(Carrier<float>), typeof(Carrier<double>), typeof(Carrier<decimal>), typeof(Carrier<char>),
        typeof(Carrier<bool>), typeof(Carrier<DateTime>), typeof(Carrier<DateTimeOffset>), typeof(Carrier<TimeSpan>),
        typeof(Carrier<Guid>), typeof(Carrier<Uri>), typeof(Carrier<string>), typeof(Carrier<object>),
        typeof(Keyed), typeof(Keyless), typeof(Leaf), typeof(Carrier<Keyed>),
    ];

    private static string NameOf(int i) => $"Overlay{i}";

    /// <summary>
    /// A hub that re-registers N of its parent's types, under the parent's names, adds 0 definitions —
    /// and resolves each of them to the parent's very definition, by type, by name and by key.
    /// Fails on the code before the change, which held a definition per keyless type per hub.
    /// </summary>
    [Fact]
    public void AHubReRegisteringItsParentsTypes_AddsNoDefinitions()
    {
        ManyTypes.Should().HaveCount(TypeCount);
        var mesh = MessageHubExtensions.CreateTypeRegistry();
        for (var i = 0; i < TypeCount; i++)
            mesh.WithType(ManyTypes[i], NameOf(i));

        var hub = MessageHubExtensions.CreateTypeRegistry(mesh);
        for (var i = 0; i < TypeCount; i++)
        {
            hub.WithType(ManyTypes[i], NameOf(i));
            hub.WithType(ManyTypes[i], NameOf(i));
        }

        hub.OwnTypes.Should().BeEmpty("every registration is identical to the parent's: none is an override");
        for (var i = 0; i < TypeCount; i++)
        {
            var meshDefinition = mesh.GetTypeDefinition(ManyTypes[i], create: false);
            hub.GetTypeDefinition(ManyTypes[i], create: false).Should().BeSameAs(meshDefinition);
            hub.GetTypeDefinition(NameOf(i)).Should().BeSameAs(meshDefinition);
            hub.GetCollectionName(ManyTypes[i]).Should().Be(NameOf(i));
        }
        hub.GetKeyFunction(typeof(Keyed))!.Function(new Keyed("k1", "x")).Should().Be("k1");
    }

    /// <summary>
    /// An identical re-registration of a type the hub itself added returns the existing definition:
    /// one entry, one definition, and the full-name alias resolves the same object as the name. Fails
    /// on the code before the change, where the second <c>WithType</c> built a second definition and
    /// the alias kept the first.
    /// </summary>
    [Fact]
    public void AnIdenticalReRegistration_ReturnsTheExistingDefinition()
    {
        var mesh = MessageHubExtensions.CreateTypeRegistry();
        var hub = MessageHubExtensions.CreateTypeRegistry(mesh);

        hub.WithType(typeof(Keyless), nameof(Keyless));
        var first = hub.GetTypeDefinition(typeof(Keyless), create: false);
        hub.WithType(typeof(Keyless), nameof(Keyless));
        hub.WithType(typeof(Keyless));

        hub.OwnTypes.Should().ContainSingle("the type is new to the chain once, and identical after that");
        hub.GetTypeDefinition(typeof(Keyless), create: false).Should().BeSameAs(first);
        hub.GetTypeDefinition(typeof(Keyless).FullName!.Replace('+', '.')).Should().BeSameAs(first,
            "the full-name alias and the name resolve one definition");
        mesh.GetTypeDefinition(typeof(Keyless), create: false).Should().BeNull("the parent never learns a hub's type");
    }

    /// <summary>
    /// Every plain hosted hub adds nothing of its own: the address type and the hub's built-in message
    /// types are all resolved through the mesh. Fails on the code before the change (two definitions
    /// per built-in type per hub).
    /// </summary>
    [Fact]
    public void APlainHostedHub_HoldsNoTypeDefinitions()
    {
        var hubs = Enumerable.Range(0, 8)
            .Select(i => Mesh.GetHostedHub(new Address("overlay", i.ToString()),
                c => c.WithPostingIdentity(PostingIdentity.System)))
            .ToArray();

        foreach (var hub in hubs)
        {
            var own = hub.TypeRegistry.OwnTypes.Select(t => t.Key).ToArray();
            Output.WriteLine($"{hub.Address}: {own.Length} own entries [{string.Join(", ", own)}]");
            own.Should().BeEmpty("a hub with no overrides holds 0 entries");
        }
        hubs[0].TypeRegistry.GetCollectionName(typeof(PingRequest)).Should().Be(nameof(PingRequest));
        hubs[0].TypeRegistry.GetTypeDefinition(typeof(PingRequest), create: false)
            .Should().BeSameAs(Mesh.TypeRegistry.GetTypeDefinition(typeof(PingRequest), create: false));
    }

    /// <summary>
    /// A hub's override wins locally — by type, by name, in the enumeration — while its sibling and
    /// its parent are unaffected; and the override is the ONLY entry the hub holds.
    /// </summary>
    [Fact]
    public void AHubOverride_WinsLocally_AndLeavesSiblingsAndParentAlone()
    {
        var mesh = MessageHubExtensions.CreateTypeRegistry()
            .WithType(typeof(Keyed), nameof(Keyed))
            .WithType(typeof(Keyless), nameof(Keyless));
        var overriding = MessageHubExtensions.CreateTypeRegistry(mesh)
            .WithType(typeof(Keyed), "Renamed")
            .WithType(typeof(Keyless), nameof(Keyless));
        var sibling = MessageHubExtensions.CreateTypeRegistry(mesh)
            .WithType(typeof(Keyed), nameof(Keyed));

        overriding.OwnTypes.Select(t => t.Key).Should().Equal(["Renamed"], "only the override is held");
        overriding.GetCollectionName(typeof(Keyed)).Should().Be("Renamed");
        overriding.GetType("Renamed").Should().Be(typeof(Keyed));
        overriding.Types.Should().Contain(t => t.Key == "Renamed");

        sibling.OwnTypes.Should().BeEmpty();
        sibling.GetCollectionName(typeof(Keyed)).Should().Be(nameof(Keyed));
        sibling.GetType("Renamed").Should().BeNull("a sibling's override is not visible");
        mesh.GetCollectionName(typeof(Keyed)).Should().Be(nameof(Keyed));
        mesh.GetType("Renamed").Should().BeNull("the parent never learns a hub's override");
    }

    /// <summary>
    /// Lookup through a three-level chain: each level adds one type, the leaf resolves all three by
    /// type, name and key, the middle does not see the leaf's, and the leaf's enumeration is the merged
    /// view with the parent's entries first and its own override in place of the parent's.
    /// </summary>
    [Fact]
    public void ALookupThroughAThreeLevelChain_ResolvesEveryLevel()
    {
        var root = MessageHubExtensions.CreateTypeRegistry()
            .WithType(typeof(Keyed), nameof(Keyed))
            .WithType(typeof(Carrier<>));
        var middle = MessageHubExtensions.CreateTypeRegistry(root).WithType(typeof(Keyless), nameof(Keyless));
        var leaf = MessageHubExtensions.CreateTypeRegistry(middle).WithType(typeof(Leaf), nameof(Leaf));

        leaf.GetType(nameof(Keyed)).Should().Be(typeof(Keyed));
        leaf.GetType(nameof(Keyless)).Should().Be(typeof(Keyless));
        leaf.GetType(nameof(Leaf)).Should().Be(typeof(Leaf));
        leaf.GetCollectionName(typeof(Keyed)).Should().Be(nameof(Keyed));
        leaf.GetTypeDefinition(typeof(Keyed), create: false).Should()
            .BeSameAs(root.GetTypeDefinition(typeof(Keyed), create: false));
        leaf.GetKeyFunction(typeof(Leaf))!.Function(new Leaf("l1")).Should().Be("l1");
        leaf.GetKeyFunction(nameof(Keyed))!.Function(new Keyed("k1", "x")).Should().Be("k1");
        leaf.TryGetType($"{nameof(Carrier<int>)}`1[{nameof(Keyed)}]", out var generic).Should().BeTrue();
        generic!.Type.Should().Be(typeof(Carrier<Keyed>), "a generic name is synthesised from every level's names");

        middle.GetType(nameof(Leaf)).Should().BeNull();
        leaf.OwnTypes.Select(t => t.Key).Should().Equal([nameof(Leaf)]);
        middle.OwnTypes.Select(t => t.Key).Should().Equal([nameof(Keyless)]);

        var names = leaf.Types.Select(t => t.Key).ToArray();
        names.Should().OnlyHaveUniqueItems();
        names.Should().Contain([nameof(Keyed), nameof(Keyless), nameof(Leaf)]);
        Array.IndexOf(names, nameof(Keyed)).Should().BeLessThan(Array.IndexOf(names, nameof(Keyless)), "parent first");
        Array.IndexOf(names, nameof(Keyless)).Should().BeLessThan(Array.IndexOf(names, nameof(Leaf)), "own layer last");

        var overriding = MessageHubExtensions.CreateTypeRegistry(middle).WithType(typeof(Keyed), nameof(Keyless));
        overriding.Types.Single(t => t.Key == nameof(Keyless)).Value.Type.Should().Be(typeof(Keyed),
            "an own entry replaces the parent's entry of the same name in the merged view");
    }

    /// <summary>
    /// A collectible type registered on a hub is held by that hub's layer only: the root never resolves
    /// it, and once the hub's registry is gone and the load context unloads, nothing keeps it alive.
    /// </summary>
    [Fact]
    public void ACollectibleTypeRegisteredOnAHub_IsNotRetainedByTheRoot()
    {
        var root = MessageHubExtensions.CreateTypeRegistry().WithType(typeof(Keyed), nameof(Keyed));
        var context = RegisterCollectibleOnAChild(root, out var registeredName);

        root.GetType(registeredName).Should().BeNull("the root never learns a hub's collectible type");
        root.Types.Should().NotContain(t => t.Key == registeredName);

        for (var i = 0; i < 20 && context.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        context.IsAlive.Should().BeFalse("nothing reachable from the root registry holds the collectible type");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterCollectibleOnAChild(ITypeRegistry root, out string registeredName)
    {
        var context = new AssemblyLoadContext("overlay-collectible", isCollectible: true);
        var type = context.LoadFromAssemblyPath(typeof(Keyed).Assembly.Location).GetType(typeof(Keyed).FullName!)!;
        registeredName = "CollectibleKeyed";
        var hub = MessageHubExtensions.CreateTypeRegistry(root).WithType(type, registeredName);
        hub.GetType(registeredName).Should().Be(type);
        hub.OwnTypes.Should().ContainSingle();
        root.GetTypeDefinition(type, create: false).Should().BeNull();
        context.Unload();
        return new WeakReference(context);
    }
}
