using System;
using System.IO;
using MeshWeaver.Graph.Configuration;
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
}
