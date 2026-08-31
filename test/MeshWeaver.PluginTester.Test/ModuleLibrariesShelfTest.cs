using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The module-libraries shelf: additional libraries resolve from a curated, deps.json-recorded
/// publish — and every ride is DERIVED from that record, minus what the landing image supplies.
/// The 2026-08-19/20 outage was a guessed closure; these tests pin the property that replaces the
/// guessing.
/// </summary>
public class ModuleLibrariesShelfTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mw-shelf-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>A shelf with one provider package chaining to a transitive and to a platform lib.</summary>
    private string Shelf()
    {
        Directory.CreateDirectory(_root);
        foreach (var name in new[] { "Provider.Sdk", "Provider.Transitive", "Azure.Core" })
            File.WriteAllBytes(Path.Combine(_root, name + ".dll"), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(_root, "MeshWeaver.ModuleLibraries.deps.json"), """
            {
              "targets": {
                "net10.0": {
                  "Provider.Sdk/2.0.0": {
                    "runtime": { "lib/net10.0/Provider.Sdk.dll": {} },
                    "dependencies": { "Provider.Transitive": "1.0.0", "Azure.Core": "1.60.0" }
                  },
                  "Provider.Transitive/1.0.0": {
                    "runtime": { "lib/net10.0/Provider.Transitive.dll": {} }
                  },
                  "Azure.Core/1.60.0": {
                    "runtime": { "lib/net10.0/Azure.Core.dll": {} }
                  }
                }
              }
            }
            """);
        return _root;
    }

    [Fact]
    public void ARideIsTheDepsRecordedClosureMinusWhatTheImageSupplies()
    {
        var shelf = ModuleLibrariesShelf.Read(Shelf());

        // Azure.Core is in the landing image — it must never ride (a same-identity duplicate
        // beside the platform's copy is the #143 binding trap, not a convenience).
        var resolution = shelf.Resolve(
            "Provider.Sdk",
            suppliedByContainer: n => n.Equals("Azure.Core", StringComparison.OrdinalIgnoreCase));

        resolution.Should().NotBeNull();
        resolution!.Version.Should().Be("2.0.0", "the shelf's pin is the same central pin the SDK path used");
        resolution.ReferenceFiles.Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .Should().Equal("Provider.Sdk.dll");
        resolution.RideFiles.Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .Should().Equal(["Provider.Sdk.dll", "Provider.Transitive.dll"],
                "the transitive shelf closure rides; the image-supplied dependency does not");
    }

    [Fact]
    public void APackageTheShelfDoesNotCarryResolvesToNull_neverToAGuess()
        => ModuleLibrariesShelf.Read(Shelf())
            .Resolve("Some.Unknown.Package", _ => false)
            .Should().BeNull("the unresolved-package refusal stays the answer for everything uncurated");

    [Fact]
    public void AShelfWithoutItsDepsRecordIsARefusal()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "Whatever.dll"), [0x4D, 0x5A]);

        Action act = () => ModuleLibrariesShelf.Read(_root);

        act.Should().Throw<InvalidOperationException>().WithMessage("*deps.json*",
            "a shelf that silently resolves nothing turns every consumer's failure into a lie");
    }
}
