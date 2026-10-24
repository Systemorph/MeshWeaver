using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using MeshWeaver.Fixture;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the per-hub DI cost (#7082, <c>Doc/Architecture/PortalHeapIsHubs</c> → "Per-hub DI and type
/// metadata"). Every hub owns an Autofac child scope, and a child scope answers a service its parent
/// provides by wrapping each parent registration of it in an <c>ExternalComponentRegistration</c> — with
/// its own resolve-pipeline builder and middleware declarations — kept for the life of the hub. The OOM
/// dump of the memex portal held 12,060,761 of them (5.78 GiB of Autofac rows).
///
/// <para><b>Measured before the fix</b>, on a node hub with the standard data-source setup in a test
/// mesh: 125 wrappers, 26 of them for <c>AutoActivate</c>; on a hosted child of it 48, again 26 for
/// <c>AutoActivate</c>. Those 26 are every instance registration of the mesh: Autofac's startable pass
/// ran on every hub scope (the scope's property bag falls back to the root's build options) and asked the
/// CHILD registry for <c>AutoActivate</c> — wrapping every <c>RegisterInstance</c> in the chain, each of
/// which was already activated in its own scope, so the pass started nothing. The wrapper count of a hub
/// therefore grew with every <c>AddSingleton(instance)</c> any module made at the root. After the fix:
/// 99 and 22 — what the hub actually resolves.</para>
///
/// <para>The negative controls are what make the measurement mean the right thing: a hub's OWN
/// registration still overrides the parent's (for the hub, for a parent-scoped service resolved in the
/// hub, and in the enumeration order), and a hub's own startable component is still started.</para>
/// </summary>
public class HubScopeDeclaresOnlyItsOwnServicesTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>Instance registrations at the root — the shape every <c>AddSingleton(instance)</c> has.</summary>
    private const int RootInstances = 200;

    private const string ExternalRegistration = "ExternalComponentRegistration";

    public sealed record Marker(int Index);
    public interface IGreeting { string Text { get; } }
    public sealed record Greeting(string Text) : IGreeting;
    public sealed class GreetingReader(IGreeting greeting) { public string Text => greeting.Text; }
    public sealed record StartCounter { public int Starts { get; set; } }
    public sealed class CountingStartable(StartCounter counter) : IStartable { public void Start() => counter.Starts++; }

    private static int Wrappers(IServiceProvider provider) =>
        provider.GetRequiredService<ILifetimeScope>().ComponentRegistry.Registrations
            .Count(r => r.GetType().Name == ExternalRegistration);

    private static IServiceProvider RootWith(int instances, Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        for (var i = 0; i < instances; i++)
            services.AddSingleton(new Marker(i));
        more?.Invoke(services);
        return services.CreateMeshWeaverServiceProvider();
    }

    /// <summary>
    /// A child scope's wrappers do not grow with the root's instance registrations. Fails on the code
    /// before the fix: there the child of the 200-instance root held 200 MORE wrappers than the child of
    /// the empty root, one per instance, while resolving exactly the same services.
    /// </summary>
    [Fact]
    public void AChildScope_DoesNotRedeclareTheRootsInstanceRegistrations()
    {
        int ChildWrappers(int instances)
        {
            var root = RootWith(instances, s => s.AddSingleton(new Greeting("root")));
            var child = new ServiceCollection().AddSingleton(new StartCounter()).SetupModules(root);
            child.GetRequiredService<Greeting>().Text.Should().Be("root");
            var grandchild = new ServiceCollection().SetupModules(child);
            grandchild.GetRequiredService<StartCounter>().Should().BeSameAs(child.GetRequiredService<StartCounter>());
            return Wrappers(child) + Wrappers(grandchild);
        }

        var withoutInstances = ChildWrappers(0);
        var withInstances = ChildWrappers(RootInstances);
        Output.WriteLine($"child + grandchild wrappers: {withoutInstances} under an empty root, {withInstances} under {RootInstances} root instances");

        withInstances.Should().Be(withoutInstances,
            "a child scope wraps what it resolves, never the root's whole instance set");
        withoutInstances.Should().BeLessThan(16,
            "measured 4 after the fix: the two resolved services and the scope infrastructure");
    }

    /// <summary>
    /// The same on real hubs: a hosted hub under a mesh holding <see cref="RootInstances"/> instance
    /// registrations wraps only what it resolves. Before the fix every hosted hub held more than
    /// <see cref="RootInstances"/> wrappers.
    /// </summary>
    [Fact]
    public void AHostedHub_WrapsWhatItResolves_NotTheMeshsInstanceSet()
    {
        var hubs = Enumerable.Range(0, 8)
            .Select(i => Mesh.GetHostedHub(new Address("scopecost", i.ToString()),
                c => c.WithPostingIdentity(PostingIdentity.System)))
            .ToArray();

        var counts = hubs.Select(h => Wrappers(h.ServiceProvider)).ToArray();
        Output.WriteLine($"wrappers per hosted hub: {string.Join(", ", counts)} (mesh root holds {RootInstances} extra instances)");
        counts.Should().AllSatisfy(c => c.Should().BeLessThan(RootInstances / 2,
            $"a hosted hub resolves a few dozen services; before the fix it wrapped all {RootInstances} instances of its mesh as well"));
        hubs.Select(h => h.ServiceProvider.GetRequiredService<ILifetimeScope>().ComponentRegistry.Registrations
                .Count(r => r.Services.Any(s => s.Description == "AutoActivate")))
            .Should().AllSatisfy(c => c.Should().Be(0, "no hub scope asks its parents for their auto-activated components"));
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
        => base.ConfigureMesh(conf).WithServices(services =>
        {
            for (var i = 0; i < RootInstances; i++)
                services.AddSingleton(new Marker(i));
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
            "a parent-scoped service built in the hub scope resolves its dependencies there");
        hub.GetRequiredService<IEnumerable<IGreeting>>().Select(g => g.Text)
            .Should().Equal(new[] { "root", "hub" }, "the parent's registration is still visible, before the hub's own");
    }

    /// <summary>
    /// Negative control: a hub scope's OWN startable component is still started when the scope is built —
    /// the startable pass is skipped only when nothing in the scope itself would be started.
    /// </summary>
    [Fact]
    public void AHubsOwnStartable_IsStillStarted()
    {
        var counter = new StartCounter();
        var root = RootWith(RootInstances);
        _ = new ServiceCollection()
            .AddSingleton<IStartable>(_ => new CountingStartable(counter))
            .SetupModules(root);
        counter.Starts.Should().Be(1, "the scope's own IStartable is started as the scope is built");

        var plain = new ServiceCollection().AddSingleton(new StartCounter()).SetupModules(root);
        plain.GetRequiredService<StartCounter>().Starts.Should().Be(0);
    }
    /// <summary>
    /// A startable GRANDCHILD under a plain child is still started. The child's scope says
    /// IgnoreStartableComponents, and Autofac's property bag falls back to the parent scope's, so a
    /// grandchild that left its own value unset inherited the flag and never ran <c>Start()</c>.
    /// </summary>
    [Fact]
    public void AStartableGrandchild_UnderAPlainChild_IsStillStarted()
    {
        var counter = new StartCounter();
        var root = RootWith(RootInstances);
        var plainChild = new ServiceCollection().AddSingleton(new Greeting("child")).SetupModules(root);
        _ = new ServiceCollection()
            .AddSingleton<IStartable>(_ => new CountingStartable(counter))
            .SetupModules(plainChild);
        counter.Starts.Should().Be(1, "the grandchild's own IStartable is started as its scope is built");
    }
}
