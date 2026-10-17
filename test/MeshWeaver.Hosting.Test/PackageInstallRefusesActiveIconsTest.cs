using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The package installer's bulk lanes write to persistence directly, past the creation validators,
/// so they apply the icon rule (<see cref="IconSafety"/>) themselves: a package carrying an icon the
/// rule refuses fails its install naming the node, before anything is written. Pinned here on the
/// helper both lanes call; the rule itself is pinned by <c>IconSafetyTest</c> (MeshWeaver.Graph.Test).
/// </summary>
public sealed class PackageInstallRefusesActiveIconsTest
{
    private const string Clean =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M4 4h16\"/></svg>";
    private const string Active =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"void 0\"><path d=\"M4 4h16\"/></svg>";

    private static MeshNode Node(string id, string? icon) => new(id, "Pkg") { Name = id, Icon = icon };

    [Fact]
    public void ABatchOfAcceptableIcons_IsWritten()
        => Assert.Null(PackageInstaller.RefusedIcon([Node("a", Clean), Node("b", "🤖"), Node("c", null)]));

    [Fact]
    public void ABatchCarryingARefusedIcon_FailsTheInstall_NamingTheNode()
    {
        var refusal = PackageInstaller.RefusedIcon([Node("a", Clean), Node("bad", Active)]);
        Assert.NotNull(refusal);
        Assert.Contains("Pkg/bad", refusal!.Message);
        Assert.Contains("onload", refusal.Message);
    }
}
