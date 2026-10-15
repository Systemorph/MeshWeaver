using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#6793 — <b>a withdrawn module's query provider leaves the query fan-out; it never stops the
/// host.</b> A module registers an <see cref="IMeshQueryProvider"/>, a platform interface, so the root holds
/// a forwarding proxy for it. When the module is WITHDRAWN at boot (a singleton of it cannot be built —
/// on the instance, a mail sender whose tenant id the configuration no longer carried), that proxy stays in
/// the root's <c>IEnumerable&lt;IMeshQueryProvider&gt;</c>, and the query fan-out read its name through it
/// and threw <c>Module MeshWeaver.Test.BootBuilt holds no service scope in this mesh.</c>. A hosted service
/// takes <see cref="IMeshService"/>, which builds that fan-out, so the host never started: the pod
/// crash-looped on every roll while the module was reported as merely contributing nothing.
/// </summary>
public sealed class AWithdrawnModulesQueryProviderLeavesTheQueryFanOutTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    /// <inheritdoc />
    protected override bool Broken => true;

    /// <inheritdoc />
    protected override bool RegistersQueryProvider => true;

    [Fact(Timeout = 120_000)]
    public async Task TheMeshServiceBuilds_AndAQueryAnswersWithoutTheModule()
    {
        Contexts.Current(Module).Should().BeNull("the precondition: the module was withdrawn at boot");
        Incompatible.Should().ContainSingle("the withdrawal is reported where every module that failed to install is");

        var built = Record.Exception(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>());
        built.Should().BeNull("a withdrawn module's query provider leaves the fan-out; it never faults building IMeshService");

        await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{QueriedProbePath}").AsSystem())
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Select(change => change.Items.Any(node => node.Path == QueriedProbePath))
            .Where(answeredByTheModule => !answeredByTheModule)
            .Should().Within(60.Seconds())
            .Emit("a query answers while an unrelated module is withdrawn, and the withdrawn module contributes nothing",
                cancellationToken: TestContext.Current.CancellationToken);
    }
}

/// <summary>
/// The negative control: the same module, built cleanly, contributes its query provider and the fan-out
/// answers with its node — so the withdrawn twin's absence is the withdrawal, not a provider never
/// registered or never asked.
/// </summary>
public sealed class APublishedModulesQueryProviderAnswersInTheFanOutTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    /// <inheritdoc />
    protected override bool Broken => false;

    /// <inheritdoc />
    protected override bool RegistersQueryProvider => true;

    [Fact(Timeout = 120_000)]
    public async Task TheModulesProviderAnswers()
    {
        Contexts.Current(Module).Should().NotBeNull();

        await Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{QueriedProbePath}").AsSystem())
            .Where(change => change.Items.Any(node => node.Path == QueriedProbePath))
            .Should().Within(60.Seconds())
            .Emit("a published module's query provider is a member of the fan-out and answers through its proxy",
                cancellationToken: TestContext.Current.CancellationToken);
    }
}
