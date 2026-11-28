using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A package that is BOTH <c>preInstalled</c> and <c>planGated</c> must not be healed open by the
/// declared-access step: the Store's gating reconcile owns its shape, and the two alternated forever
/// (one live partition's <c>_Policy</c> past version 2,970; ~835 "gating shape is NOT STAYING" Errors a day).
/// <see cref="PackageInstaller.EnsureDeclaredAccess"/> decides this from the LIVE root through
/// <see cref="PackageInstaller.PlanGatedOnRoot"/>, which is pinned here in every shape a root arrives in.
/// </summary>
public sealed class APlanGatedRootIsLeftToTheStoreGateTest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static MeshNode Root(object? content) => new("Pkg") { NodeType = "Store/Plugin", Content = content };

    private static JsonObject Plugin(bool preInstalled, bool? planGated)
    {
        var content = new JsonObject { ["$type"] = "PluginContent", ["preInstalled"] = preInstalled };
        if (planGated is { } gated)
            content["planGated"] = gated;
        return content;
    }

    [Fact]
    public void ARootDeclaringPlanGated_IsRecognised_AsJsonObject()
        => Assert.True(PackageInstaller.PlanGatedOnRoot(Root(Plugin(true, true)), Options));

    [Fact]
    public void ARootDeclaringPlanGated_IsRecognised_AsJsonElement()
        => Assert.True(PackageInstaller.PlanGatedOnRoot(
            Root(JsonSerializer.SerializeToElement(Plugin(true, true))), Options));

    [Fact]
    public void ARootWithoutTheFlag_IsNotPlanGated_SoThePreInstalledBaselineStaysOpen()
    {
        Assert.False(PackageInstaller.PlanGatedOnRoot(Root(Plugin(true, null)), Options));
        Assert.False(PackageInstaller.PlanGatedOnRoot(Root(Plugin(true, false)), Options));
        Assert.True(PackageInstaller.PreInstalledOnRoot(Root(Plugin(true, true)), Options));
    }

    [Fact]
    public void AnAbsentOrForeignRoot_DecidesNothing_SoAFirstInstallProceedsAsBefore()
    {
        Assert.Null(PackageInstaller.PlanGatedOnRoot(null, Options));
        Assert.Null(PackageInstaller.PlanGatedOnRoot(Root(null), Options));
        Assert.Null(PackageInstaller.PlanGatedOnRoot(Root(new JsonObject
        {
            ["$type"] = "Markdown", ["planGated"] = true,
        }), Options));
    }
}
