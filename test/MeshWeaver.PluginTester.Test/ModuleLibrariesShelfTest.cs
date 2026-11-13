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
            .Should().Equal(["Provider.Sdk.dll", "Provider.Transitive.dll"],
                "the compile surface is the package's TRANSITIVE closure, exactly as the SDK "
                + "hands it to a consumer — Mail.MicrosoftGraph rode Kiota but could not compile "
                + "against it when this offered only the package's own assemblies (Plugins#1032)");
        resolution.RideFiles.Select(Path.GetFileName).Order(StringComparer.Ordinal)
            .Should().Equal(["Provider.Sdk.dll", "Provider.Transitive.dll"],
                "the transitive shelf closure rides; the image-supplied dependency does not");
    }

    /// <summary>
    /// The flat shelf's files carry no package in their path, so the dependency graph names a
    /// Package edge by the shelf's deps.json ownership (<c>Doc/Architecture/DependencyGraph</c>): a
    /// package whose assembly is named differently from it (<c>Acme.Bundle</c> shipping
    /// <c>Acme.Widget.dll</c>) and a transitive keep their OWN package ids; an assembly no package
    /// claims has no owner rather than a guessed one.
    /// </summary>
    [Fact]
    public void OwnerOf_IsTheDepsRecordedPackage_NotTheFileName()
    {
        var root = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "Acme.Widget", "Acme.Transitive" })
            File.WriteAllBytes(Path.Combine(root, name + ".dll"), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(root, "MeshWeaver.ModuleLibraries.deps.json"), """
            {
              "targets": {
                "net10.0": {
                  "Acme.Bundle/1.0.0": {
                    "runtime": { "lib/net10.0/Acme.Widget.dll": {} },
                    "dependencies": { "Acme.Transitive": "1.0.0" }
                  },
                  "Acme.Transitive/1.0.0": {
                    "runtime": { "lib/net10.0/Acme.Transitive.dll": {} }
                  }
                }
              }
            }
            """);
        var shelf = ModuleLibrariesShelf.Read(root);

        shelf.OwnerOf("Acme.Widget").Should().Be("Acme.Bundle", "the package, not the file's simple name");
        shelf.OwnerOf("Acme.Transitive").Should().Be("Acme.Transitive", "a transitive keeps its own owner");
        shelf.OwnerOf("Acme.Unclaimed").Should().BeNull("no package claims it — never a guess");
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

    [Fact]
    public void ANamedShelfReplacesTheSearch()
    {
        // The app directory is empty: only the named directory can have supplied this shelf.
        var emptyApp = Path.Combine(_root, "empty-app");
        var named = Shelf();
        Directory.CreateDirectory(emptyApp);

        var shelf = ModuleLibrariesShelf.Locate(named, emptyApp);

        shelf.Should().NotBeNull();
        shelf!.Directory.Should().Be(Path.GetFullPath(named),
            "a module repository that publishes its own shelf resolves from that publish");
        shelf.Resolve("Provider.Sdk", _ => false).Should().NotBeNull();
    }

    [Fact]
    public void ANamedShelfThatIsMissingIsARefusal_neverAFallbackToTheImagesShelf()
    {
        var missing = Path.Combine(_root, "not-published");

        Action act = () => ModuleLibrariesShelf.Locate(missing, _root);

        act.Should().Throw<InvalidOperationException>().WithMessage("*--module-libs*does not exist*",
            "the caller named a shelf; resolving from another one would build against a list it did not declare");
    }
}
