using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the per-hub DI cost (#7082, #7145, <c>Doc/Architecture/PortalHeapIsHubs</c> → "Per-hub DI and
/// type metadata"). A hub's level of <see cref="MeshServiceProvider"/> holds exactly the descriptors the
/// hub itself declared, frozen when the hub is built, and the instances it built — nothing of its
/// parents'. The container this replaced kept, per hub, one wrapper registration (with its own resolve
/// pipeline) for every parent registration the hub resolved: 99 per node hub, 15 per hosted hub after
/// #7131, ~120 per hub in the memex OOM dump.
///
/// <para>The negative controls are what make the measurement mean the right thing: a hub's OWN
/// registration still overrides the parent's (directly, through a parent-scoped service resolved in the
/// hub, and in the enumeration order), and a parent's scoped service resolved in a real hosted hub is
/// built with THAT hub's own <see cref="IMessageHub"/> — the per-hub <c>INodeValidator</c> shape.</para>
/// </summary>
public class HubScopeDeclaresOnlyItsOwnServicesTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>Instance registrations at the root — the shape every <c>AddSingleton(instance)</c> has.</summary>
    private const int RootInstances = 200;

    public sealed record Marker(int Index);
    public interface IGreeting { string Text { get; } }
    public sealed record Greeting(string Text) : IGreeting;
    public sealed class GreetingReader(IGreeting greeting) { public string Text => greeting.Text; }
    public sealed class HubBound(IMessageHub hub) { public IMessageHub Hub { get; } = hub; }
    public sealed class Tracked : IDisposable { public bool Disposed { get; private set; } public void Dispose() => Disposed = true; }

    private static MeshServiceProvider Level(IServiceProvider provider) =>
        MeshServiceProvider.From(provider) ?? throw new InvalidOperationException("not a MeshWeaver container");

    private static IServiceProvider RootWith(int instances, Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        for (var i = 0; i < instances; i++)
            services.AddSingleton(new Marker(i));
        more?.Invoke(services);
        return services.CreateMeshWeaverServiceProvider();
    }

    /// <summary>
    /// A child level holds its own descriptors and nothing else — whatever the root holds. Under the
    /// container this replaced, the child of a 200-instance root held 200 more wrappers than the child of
    /// an empty root (#7131), and after that fix still one wrapper per service it resolved.
    /// </summary>
    [Fact]
    public void AChildLevel_HoldsOnlyItsOwnDescriptors()
    {
        (int Own, int Cached) Child(int instances)
        {
            var root = RootWith(instances, s => s.AddSingleton(new Greeting("root")));
            var child = new ServiceCollection().AddSingleton(new Tracked()).SetupModules(root);
            child.GetRequiredService<Greeting>().Text.Should().Be("root");
            var grandchild = new ServiceCollection().SetupModules(child);
            grandchild.GetRequiredService<Tracked>().Should().BeSameAs(child.GetRequiredService<Tracked>());
            grandchild.GetRequiredService<Greeting>().Should().BeSameAs(root.GetRequiredService<Greeting>(),
                "a root singleton is the root's one instance, whoever asks");
            return (Level(child).OwnRegistrationCount + Level(grandchild).OwnRegistrationCount,
                Level(child).CachedInstanceCount + Level(grandchild).CachedInstanceCount);
        }

        var withoutInstances = Child(0);
        var withInstances = Child(RootInstances);
        Output.WriteLine($"child + grandchild: {withoutInstances.Own} own registrations, {withoutInstances.Cached} cached instances "
                         + $"under an empty root; {withInstances.Own} / {withInstances.Cached} under {RootInstances} root instances");

        withInstances.Should().Be(withoutInstances, "a level holds what it declared and built, never its root's set");
        withoutInstances.Own.Should().Be(1, "the child declared one service and the grandchild none");
        withoutInstances.Cached.Should().Be(0,
            "a root singleton lives in the root and a provided instance needs no slot: nothing is cached in the levels");
    }

    /// <summary>
    /// The same on real hubs: a hosted hub under a mesh holding <see cref="RootInstances"/> instance
    /// registrations holds only the descriptors its own configuration declared — not one of them is a
    /// parent's descriptor, so there is nothing per hub that stands for a parent's registration.
    /// </summary>
    [Fact]
    public void AHostedHub_HoldsNoRegistrationOfItsParents()
    {
        var hubs = Enumerable.Range(0, 8)
            .Select(i => Mesh.GetHostedHub(new Address("scopecost", i.ToString()),
                c => c.WithPostingIdentity(PostingIdentity.System)))
            .ToArray();

        var parentDescriptors = new HashSet<ServiceDescriptor>(
            Level(Mesh.ServiceProvider).OwnRegistrations.Select(r => r.Descriptor).OfType<ServiceDescriptor>()
                .Concat(Level(Mesh.ServiceProvider).ContainerRoot.OwnRegistrations.Select(r => r.Descriptor).OfType<ServiceDescriptor>()),
            ReferenceEqualityComparer.Instance);
        var levels = hubs.Select(h => Level(h.ServiceProvider)).ToArray();
        Output.WriteLine("own registrations per hosted hub: " + string.Join(", ", levels.Select(l => l.OwnRegistrationCount))
                         + $"; cached instances: {string.Join(", ", levels.Select(l => l.CachedInstanceCount))} "
                         + $"(mesh root holds {RootInstances} extra instances)");

        levels.Should().AllSatisfy(level =>
        {
            level.IsContainerRoot.Should().BeFalse("a hosted hub is a level of its mesh's container");
            level.OwnRegistrations.Where(r => r.Descriptor is not null && parentDescriptors.Contains(r.Descriptor))
                .Should().BeEmpty("a hub level holds no registration of its parents — zero wrappers per hub");
            level.OwnRegistrationCount.Should().BeLessThan(RootInstances / 2,
                $"a hosted hub declares a few dozen services of its own; its mesh's {RootInstances} instances are not among them");
        });
        levels.Select(l => l.OwnRegistrationCount).Distinct().Should().ContainSingle(
            "every hosted hub of the same configuration declares the same services");
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
        => base.ConfigureMesh(conf).WithServices(services =>
        {
            for (var i = 0; i < RootInstances; i++)
                services.AddSingleton(new Marker(i));
            services.AddScoped<HubBound>();
            return services;
        });

    /// <summary>
    /// Negative control: a hub's own registration still wins over the parent's — resolved directly,
    /// through a parent-scoped service resolved in the hub, and as the last of the enumeration.
    /// </summary>
    [Fact]
    public void AHubLocalRegistration_StillOverridesTheParents()
    {
        var root = RootWith(RootInstances, s => s
            .AddSingleton<IGreeting>(new Greeting("root"))
            .AddScoped<GreetingReader>());
        var hub = new ServiceCollection().AddSingleton<IGreeting>(new Greeting("hub")).SetupModules(root);

        root.GetRequiredService<IGreeting>().Text.Should().Be("root");
        hub.GetRequiredService<IGreeting>().Text.Should().Be("hub", "the hub's own registration wins in the hub");
        hub.GetRequiredService<GreetingReader>().Text.Should().Be("hub",
            "a parent-scoped service built in the hub resolves its dependencies there");
        hub.GetRequiredService<IEnumerable<IGreeting>>().Select(g => g.Text)
            .Should().Equal(new[] { "root", "hub" }, "the parent's registration is still visible, before the hub's own");
    }

    /// <summary>
    /// Negative control on real hubs: a ROOT-scoped service resolved in a hosted hub is built with that
    /// hub's own <see cref="IMessageHub"/>, one instance per hub — and resolved on the mesh hub, with the
    /// mesh hub's. This is the per-hub <c>AddScoped&lt;INodeValidator, RlsNodeValidator&gt;()</c> shape.
    /// </summary>
    [Fact]
    public void ARootScopedService_ResolvedInAHostedHub_GetsThatHubsOwnMessageHub()
    {
        var first = Mesh.GetHostedHub(new Address("scopebound", "1"), c => c.WithPostingIdentity(PostingIdentity.System));
        var second = Mesh.GetHostedHub(new Address("scopebound", "2"), c => c.WithPostingIdentity(PostingIdentity.System));

        first.ServiceProvider.GetRequiredService<HubBound>().Hub.Should().BeSameAs(first);
        second.ServiceProvider.GetRequiredService<HubBound>().Hub.Should().BeSameAs(second);
        first.ServiceProvider.GetRequiredService<HubBound>().Should().BeSameAs(first.ServiceProvider.GetRequiredService<HubBound>(),
            "scoped: one instance per hub");
        Mesh.ServiceProvider.GetRequiredService<HubBound>().Hub.Should().BeSameAs(Mesh,
            "resolved on the mesh hub it is the mesh hub's");
    }

    /// <summary>
    /// A level disposes what IT built and nothing else: its own singleton and the parent-scoped instance
    /// it built, never the root's singleton it was served, never a provided instance.
    /// </summary>
    [Fact]
    public void ALevel_DisposesOnlyWhatItBuilt()
    {
        var root = RootWith(0, s => s.AddSingleton<RootTracked>().AddScoped<ScopedTracked>());
        var provided = new Tracked();
        var level = new ServiceCollection().AddSingleton<HubTracked>().AddSingleton(provided).SetupModules(root);
        var rootSingleton = level.GetRequiredService<RootTracked>();
        var scoped = level.GetRequiredService<ScopedTracked>();
        var own = level.GetRequiredService<HubTracked>();

        ((IDisposable)level).Dispose();

        own.Disposed.Should().BeTrue("the level built its own singleton");
        scoped.Disposed.Should().BeTrue("the level built the parent-scoped instance it resolved");
        rootSingleton.Disposed.Should().BeFalse("the root's singleton belongs to the root");
        provided.Disposed.Should().BeFalse("a provided instance is never the container's to dispose");
        ((Action)(() => level.GetService(typeof(HubTracked)))).Should().Throw<ObjectDisposedException>("an ended scope refuses every service it can answer")
            .Which.ObjectName.Should().Be(MeshServiceProvider.DisposedObjectName);
    }

    public sealed class RootTracked : IDisposable { public bool Disposed { get; private set; } public void Dispose() => Disposed = true; }
    public sealed class ScopedTracked : IDisposable { public bool Disposed { get; private set; } public void Dispose() => Disposed = true; }
    public sealed class HubTracked : IDisposable { public bool Disposed { get; private set; } public void Dispose() => Disposed = true; }
}
