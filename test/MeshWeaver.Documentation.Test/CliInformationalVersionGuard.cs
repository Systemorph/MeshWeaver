using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <c>memex --version</c> names the CLI's OWN release, never the platform's — policy
/// <c>cli-thin-wrapper-own-version</c> (<c>Doc/Architecture/NuGetPackageRetirement</c> §1a).
///
/// <para><c>Directory.Build.props</c> sets <c>InformationalVersion</c> before the CLI's csproj
/// body runs — to <c>$(PlatformVersion)</c> under <c>CIRun</c>, to the inherited
/// <c>$(Version)</c> otherwise — so a csproj that declares only its own <c>&lt;Version&gt;</c>
/// packs CLI 3.0.0 while <c>memex --version</c> prints the platform line (#6860 review). This
/// evaluates the real project through the real evaluator, in both build modes, and also with a
/// global <c>-p:Version</c> (what CD passes), which must not reach the compiled attribute.</para>
/// </summary>
public class CliInformationalVersionGuard
{
    private const string CliProject = "src/MeshWeaver.Cli/MeshWeaver.Cli.csproj";

    [Theory]
    [InlineData("-p:CIRun=true")]
    [InlineData("-p:CIRun=false")]
    public void InformationalVersion_IsTheClisOwnVersion(string mode)
    {
        var properties = MsBuildPropertyProbe.Evaluate(
            CliProject, ["CliVersion", "Version", "InformationalVersion", "PlatformVersion"], mode);

        var cli = properties["CliVersion"];
        Assert.False(string.IsNullOrEmpty(cli),
            "MeshWeaver.Cli.csproj declares its own release as <CliVersion>");
        Assert.True(properties["Version"] == cli,
            $"the package version must be the CLI's own version {cli} (publish-packages.yml reads "
            + $"-getProperty:Version), but under {mode} it is {properties["Version"]}");
        Assert.True(properties["InformationalVersion"] == cli,
            $"`memex --version` prints InformationalVersion; under {mode} it is "
            + $"{properties["InformationalVersion"]} but must name the CLI's release {cli}, not the "
            + $"platform's ({properties["PlatformVersion"]})");
    }

    [Fact]
    public void AGlobalVersionOverride_DoesNotReachInformationalVersion()
    {
        var properties = MsBuildPropertyProbe.Evaluate(
            CliProject, ["CliVersion", "InformationalVersion"], "-p:Version=9.9.9-probe");

        Assert.True(properties["InformationalVersion"] == properties["CliVersion"],
            $"a compiled version attribute must not take CD's -p:Version (commit determinism, #3022): "
            + $"InformationalVersion is {properties["InformationalVersion"]}, CliVersion {properties["CliVersion"]}");
    }
}
