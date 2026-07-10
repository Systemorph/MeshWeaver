using System;
using System.Collections.Generic;
using System.IO;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.NuGet;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the "no NuGet for business rules" contract: the BusinessRules scope generator ships WITH the
/// platform (its DLL copied into the Graph runtime output, flowing to every consumer) and is
/// discoverable as a Roslyn generator — so <c>MeshNodeCompilationService</c> always feeds it to the
/// compile and an <c>IScope&lt;,&gt;</c> node type compiles with NO
/// <c>#r "nuget:MeshWeaver.BusinessRules.Generator"</c> and NO mesh-local NuGet feed / BakeMeshLocalFeed.
/// </summary>
public class BuiltInScopeGeneratorTest
{
    [Fact]
    public void ScopeGenerator_ShipsWithPlatform_AndIsDiscoverable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "MeshWeaver.BusinessRules.Generator.dll");
        File.Exists(path).Should().BeTrue(
            "the BusinessRules scope generator must ship in the runtime output so IScope<,> nodes " +
            "compile without any #r \"nuget:...\" directive");

        var generators = SourceGeneratorLoader.Discover([path], NullLogger.Instance);
        generators.Should().NotBeEmpty("the shipped DLL must expose the [Generator] ScopeCodeGenerator");
    }

    // A legacy `#r "nuget:MeshWeaver.BusinessRules.Generator"` (authored before the generator became
    // built-in) must never reach the NuGet resolver: after BakeMeshLocalFeed was removed (#395) the
    // mesh-local feed (dist/packages) is gone from deployed images, so RESOLVING it hard-fails with
    // "The local source '/app/dist/packages' doesn't exist" and breaks every deployed scope node
    // still carrying that #r (the atioz BalanceSheet failure). StripBuiltInScopeGeneratorRef drops it.

    [Fact]
    public void StripBuiltInScopeGeneratorRef_DropsLegacyGeneratorRef_WhenBuiltInPresent()
    {
        var refs = new List<NuGetPackageReference>
        {
            new("MeshWeaver.BusinessRules.Generator", null),   // legacy #r — redundant, must be dropped
            new("Newtonsoft.Json", "13.0.3"),                  // unrelated #r — must survive
        };

        MeshNodeCompilationService.StripBuiltInScopeGeneratorRef(refs, builtInPresent: true);

        refs.Should().ContainSingle("the legacy generator #r must not reach the NuGet resolver")
            .Which.Id.Should().Be("Newtonsoft.Json");
    }

    [Fact]
    public void StripBuiltInScopeGeneratorRef_IsCaseInsensitive()
    {
        var refs = new List<NuGetPackageReference> { new("meshweaver.businessrules.GENERATOR", null) };

        MeshNodeCompilationService.StripBuiltInScopeGeneratorRef(refs, builtInPresent: true);

        refs.Should().BeEmpty("the built-in generator id compare is case-insensitive");
    }

    [Fact]
    public void StripBuiltInScopeGeneratorRef_KeepsLegacyRef_WhenBuiltInAbsent()
    {
        // Fallback: a runtime that somehow lacks the built-in generator DLL must still resolve the
        // generator via the #r (the pre-built-in behaviour), so the ref is preserved.
        var refs = new List<NuGetPackageReference> { new("MeshWeaver.BusinessRules.Generator", null) };

        MeshNodeCompilationService.StripBuiltInScopeGeneratorRef(refs, builtInPresent: false);

        refs.Should().ContainSingle().Which.Id.Should().Be("MeshWeaver.BusinessRules.Generator");
    }
}
