using System.Text.Json;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The manifest's configuration declaration — what a plugin says it must be TOLD before it works,
/// which the first-run setup wizard renders instead of hard-coding a form per plugin.
///
/// <para>The two properties worth pinning are that an AUTHORED declaration survives the round trip
/// a catalog makes it take, and that a manifest without one reads as "nothing to ask" — so adoption
/// is additive and a reader that predates the field is unaffected.</para>
/// </summary>
public class PackageConfigRequirementTest
{
    [Fact]
    public void AManifestWithoutADeclarationAsksNothing()
    {
        var manifest = JsonSerializer.Deserialize<PackageManifest>("""{"Id":"Essentials"}""");
        Assert.NotNull(manifest);
        Assert.Empty(manifest!.Configuration);
    }

    [Fact]
    public void AnAuthoredDeclarationSurvivesTheRoundTrip()
    {
        // The shape an author writes, and the shape the registry serves, are the same shape.
        var json = """
        {
          "Id": "Reinsurance",
          "Configuration": [
            {
              "Key": "Reinsurance__ApiKey",
              "Secret": true,
              "Kind": "other",
              "Purpose": "pricing calls fail without it",
              "Validation": "^[A-Za-z0-9-]{20,}$",
              "ResourceHelp": "az cognitiveservices account create …"
            }
          ]
        }
        """;

        var manifest = JsonSerializer.Deserialize<PackageManifest>(json)!;
        var requirement = Assert.Single(manifest.Configuration);

        Assert.Equal("Reinsurance__ApiKey", requirement.Key);
        Assert.True(requirement.Secret);
        Assert.Equal("other", requirement.Kind);
        Assert.Contains("pricing calls fail", requirement.Purpose);
        Assert.Equal("^[A-Za-z0-9-]{20,}$", requirement.Validation);
        Assert.Contains("az cognitiveservices", requirement.ResourceHelp);

        // And it serialises back to something a later reader parses identically.
        var again = JsonSerializer.Deserialize<PackageManifest>(JsonSerializer.Serialize(manifest))!;
        Assert.Equal(manifest.Configuration, again.Configuration);
    }

    [Fact]
    public void ADeclarationDefaultsToPlainAndUnexplained()
    {
        // Only the key is required: a plugin may declare a bare key and say no more. Defaulting
        // Secret to FALSE is the safe direction — a plain value written to a vault is merely odd,
        // while a secret treated as plain would be recorded on the deployment record, which is git.
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            """{"Id":"X","Configuration":[{"Key":"X__Endpoint"}]}""")!;

        var requirement = Assert.Single(manifest.Configuration);
        Assert.False(requirement.Secret);
        Assert.Null(requirement.Kind);
        Assert.Null(requirement.Purpose);
        Assert.Null(requirement.ResourceHelp);
    }
}
