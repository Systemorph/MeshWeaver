using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Updates.Test;

/// <summary>A PLATFORM interface (this test assembly, default load context) a module implements and
/// registers as a root service — the root then holds a forwarding proxy for it.</summary>
public interface ISwapProbe
{
    /// <summary>Names the generation that answered.</summary>
    string Answer();
}

/// <summary>A second platform interface, for the scenario that removes a forwarded registration.</summary>
public interface ISecondSwapProbe
{
    /// <summary>Names the generation that answered.</summary>
    string Answer();
}

/// <summary>
/// 🚨 <b>A live module swap that moves a forwarded registration's index (MeshWeaver#6847).</b> Every root
/// forwarder holds the BOOT generation's absolute registration index and resolves THAT index in whatever
/// generation is current. On the company instance (2026-10-16 00:15Z) WhatsApp 1.3.2 → 1.4.2 inserted a
/// module-owned registration ahead of a forwarded one, the swap went live, and every hub activation that
/// leased the forwarder faulted with
/// <c>The requested service 'mw-module-service:10 (MeshWeaver.WhatsApp.IWhatsAppRecentChats)' has not been registered</c>
/// for ~6.5 h — sign-in and API-token validation included.
///
/// <para>Each scenario boots generation N with one registration layout, swaps to N+1 with another, and
/// then asks the ROOT proxy — the thing a hub activation leases. The contract: the host either re-binds
/// the forwarder to the right registration of N+1 (a live swap), or refuses the swap as restart-required
/// with N still serving. Never a stale index.</para>
///
/// <para>The proxy is asked FIRST, before the outcome is read, so that with the fix reverted the test
/// fails with the production sentence itself rather than with a mismatched outcome kind.</para>
/// </summary>
public abstract class ForwardedRegistrationIndexSwapScenario : MonolithMeshTestBase
{
    /// <summary>The module's entry-assembly name.</summary>
    protected const string Module = "MeshWeaver.Test.ForwardedIndex";

    /// <summary>A forwarded registration of <see cref="ISwapProbe"/>.</summary>
    protected const string Probe = "services.AddSingleton<MeshWeaver.Updates.Test.ISwapProbe, Probe>();";

    /// <summary>A forwarded registration of <see cref="ISecondSwapProbe"/>.</summary>
    protected const string SecondProbe = "services.AddSingleton<MeshWeaver.Updates.Test.ISecondSwapProbe, SecondProbe>();";

    /// <summary>A module-owned, unkeyed registration — the <c>IWhatsAppRecentChats</c> shape: held in the
    /// module's scope with NO <c>mw-module-service:{index}</c> key.</summary>
    protected const string Inbox = "services.AddSingleton<IInbox, Inbox>();";

    /// <summary>A second module-owned, unkeyed registration.</summary>
    protected const string Outbox = "services.AddSingleton<IOutbox, Outbox>();";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    // A field initializer: it runs before the base constructor, which calls ConfigureMesh.
    private readonly string root = Path.Combine(Path.GetTempPath(), "forwarded-index-" + Guid.NewGuid().ToString("N"));

    /// <summary>Boots the mesh with generation N.</summary>
    protected ForwardedRegistrationIndexSwapScenario(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>Generation N's registrations, in order.</summary>
    protected abstract ImmutableArray<string> BootLayout { get; }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).InstallAssemblies(Write("g1", Source(1, BootLayout)));

    private ModuleLiveUpdater Updater => Mesh.ServiceProvider.GetRequiredService<ModuleLiveUpdater>();

    /// <summary>Swaps to N+1 with <paramref name="nextLayout"/>; returns the outcome.</summary>
    protected Task<ModuleSwapOutcome> SwapTo(ImmutableArray<string> nextLayout, string reason, System.Threading.CancellationToken ct) =>
        Updater.Swap(Write("g2", Source(2, nextLayout)), reason)
            .Timeout(Budget)
            .Await(ct);

    /// <summary>The scenario's assertion for an N+1 whose forwarded index moved: the root proxy still
    /// answers — from N — and the swap is restart-required, refused by name.</summary>
    protected async Task AssertRefusedWithNServing(ImmutableArray<string> nextLayout, string reason, System.Threading.CancellationToken ct)
    {
        var probe = Mesh.ServiceProvider.GetRequiredService<ISwapProbe>();
        probe.Answer().Should().Be("probe v1");

        var outcome = await SwapTo(nextLayout, reason, ct);

        string? answer = null;
        Action ask = () => answer = probe.Answer();
        ask.Should().NotThrow(
            "the root proxy a hub activation leases must never be left pointing at a stale index (MeshWeaver#6847)");
        answer.Should().Be("probe v1", "N keeps serving through the boot proxy, at its own index");
        outcome.Kind.Should().Be(ModuleSwapKind.RestartRequired, outcome.Reason);
        outcome.Reason.Should().Contain("changed shape");
    }

    private static string Source(int version, ImmutableArray<string> layout) => $$"""
        using Microsoft.Extensions.DependencyInjection;
        [assembly: MeshWeaver.Test.ForwardedIndex.Module]
        namespace MeshWeaver.Test.ForwardedIndex;
        public sealed class Probe : MeshWeaver.Updates.Test.ISwapProbe { public string Answer() => "probe v{{version}}"; }
        public sealed class SecondProbe : MeshWeaver.Updates.Test.ISecondSwapProbe { public string Answer() => "second v{{version}}"; }
        public interface IInbox { string Read(); }
        public sealed class Inbox : IInbox { public string Read() => "inbox v{{version}}"; }
        public interface IOutbox { string Read(); }
        public sealed class Outbox : IOutbox { public string Read() => "outbox v{{version}}"; }
        public sealed class ModuleAttribute : MeshWeaver.Mesh.MeshNodeProviderAttribute
        {
            public override System.Collections.Generic.IEnumerable<MeshWeaver.Mesh.MeshNode> Nodes =>
                [new MeshWeaver.Mesh.MeshNode("ForwardedIndexProbe") { Name = "v{{version}}", NodeType = "Markdown" }
                    .WithGlobalServiceRegistry(services =>
                    {
                        {{string.Join(Environment.NewLine + "                ", layout)}}
                        return services;
                    })];
        }
        """;

    private string Write(string generation, string source)
    {
        var references = CompileReferences.Default
            .Where(r => r.Display is { } path && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Append(MetadataReference.CreateFromFile(typeof(ISwapProbe).Assembly.Location));
        var compilation = CSharpCompilation.Create(Module, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var directory = Path.Combine(root, "modules", $"{Module}@{generation}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Module + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }
}

/// <summary>N+1 ADDS a module-owned registration BEFORE the forwarded one — the WhatsApp 1.4.2 shape.</summary>
public sealed class ARegistrationAddedBeforeAForwardedOne(ITestOutputHelper output) : ForwardedRegistrationIndexSwapScenario(output)
{
    /// <inheritdoc />
    protected override ImmutableArray<string> BootLayout => [Probe];

    /// <summary>Forwarder #0 would resolve N+1's #0, the unkeyed inbox — refused instead.</summary>
    [Fact(Timeout = 180_000)]
    public Task IsRestartRequired_AndNKeepsServing() =>
        AssertRefusedWithNServing([Inbox, Probe], "test: a module-owned registration added before the forwarded one", TestContext.Current.CancellationToken);
}

/// <summary>N+1 REMOVES a module-owned registration that stood BEFORE the forwarded one.</summary>
public sealed class ARegistrationRemovedBeforeAForwardedOne(ITestOutputHelper output) : ForwardedRegistrationIndexSwapScenario(output)
{
    /// <inheritdoc />
    protected override ImmutableArray<string> BootLayout => [Inbox, Probe, Outbox];

    /// <summary>Forwarder #1 would resolve N+1's #1, the unkeyed outbox — refused instead.</summary>
    [Fact(Timeout = 180_000)]
    public Task IsRestartRequired_AndNKeepsServing() =>
        AssertRefusedWithNServing([Probe, Outbox], "test: a module-owned registration before the forwarded one removed", TestContext.Current.CancellationToken);
}

/// <summary>N+1 REORDERS: the module-owned registration moves from after the forwarded one to before it.</summary>
public sealed class ARegistrationReorderedAheadOfAForwardedOne(ITestOutputHelper output) : ForwardedRegistrationIndexSwapScenario(output)
{
    /// <inheritdoc />
    protected override ImmutableArray<string> BootLayout => [Probe, Inbox];

    /// <summary>Forwarder #0 would resolve N+1's #0, the unkeyed inbox — refused instead.</summary>
    [Fact(Timeout = 180_000)]
    public Task IsRestartRequired_AndNKeepsServing() =>
        AssertRefusedWithNServing([Inbox, Probe], "test: a module-owned registration reordered ahead of the forwarded one", TestContext.Current.CancellationToken);
}

/// <summary>N+1 REMOVES a forwarded registration that stood before another forwarded one.</summary>
public sealed class AForwardedRegistrationRemoved(ITestOutputHelper output) : ForwardedRegistrationIndexSwapScenario(output)
{
    /// <inheritdoc />
    protected override ImmutableArray<string> BootLayout => [SecondProbe, Probe];

    /// <summary>The root still holds a forwarder for the removed service, and the remaining one's index
    /// moved — refused. (The forwarded subsequence changes too, so this was refused before #6847 as well.)</summary>
    [Fact(Timeout = 180_000)]
    public Task IsRestartRequired_AndNKeepsServing() =>
        AssertRefusedWithNServing([Probe], "test: a forwarded registration removed", TestContext.Current.CancellationToken);
}

/// <summary>
/// The POSITIVE control: N+1 adds a module-owned registration AFTER the forwarded one, so no forwarded
/// index moves — the swap goes live and the SAME root proxy re-binds to N+1's registration. Without it the
/// refusals above could be a fix that refuses every swap.
/// </summary>
public sealed class ARegistrationAddedAfterTheForwardedOnes(ITestOutputHelper output) : ForwardedRegistrationIndexSwapScenario(output)
{
    /// <inheritdoc />
    protected override ImmutableArray<string> BootLayout => [Probe];

    /// <summary>Live, and the boot proxy now answers from N+1.</summary>
    [Fact(Timeout = 180_000)]
    public async Task SwapsLive_AndTheProxyReBindsToNPlusOne()
    {
        var probe = Mesh.ServiceProvider.GetRequiredService<ISwapProbe>();
        probe.Answer().Should().Be("probe v1");

        var outcome = await SwapTo([Probe, Inbox], "test: a module-owned registration added after the forwarded one", TestContext.Current.CancellationToken);

        outcome.Kind.Should().Be(ModuleSwapKind.Live, outcome.Reason);
        probe.Answer().Should().Be("probe v2", "the same proxy forwards to N+1's registration at the unchanged index");
    }
}
