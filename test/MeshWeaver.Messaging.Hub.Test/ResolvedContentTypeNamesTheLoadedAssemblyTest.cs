#pragma warning disable CS1591

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>#4158, ask 2: the per-NodeType diagnostics must name the assembly the content type
/// actually RESOLVED to</b> — and that reading has to be taken from the loaded assembly, because
/// every other coordinate on the reply is a claim written by whoever last built the type.
///
/// <para>The night this issue was filed, a module reported the newest generation, the newest
/// <c>written=</c> and types that predated two merged PRs; a stale adopted build and a stale
/// registry shelf were indistinguishable from the consumer. The one statement nobody could get was
/// which assembly the serializer was actually acting on.</para>
///
/// <para><b>The third case is the negative control.</b> The SAME type full name is present twice in
/// this process — once in the default load context, once loaded from the same file's BYTES into a
/// collectible <see cref="AssemblyLoadContext"/> (the #3732 shape a runtime-compiled NodeType
/// produces). If the reading were derived from the NAME — or from anything other than the resolved
/// <c>Type</c>'s own assembly — the two readings would be identical and that case would FAIL. It is
/// what makes the other two more than restatements of their inputs.</para>
/// </summary>
public class ResolvedContentTypeNamesTheLoadedAssemblyTest
{
    private const string NodeTypePath = "Test/ContentTypeIdentity";

    /// <summary>A content type of this test assembly — an ordinary, file-backed, non-collectible load.</summary>
    private sealed record Marker(string Value);

    [Fact]
    public void NoRegistryIsNotMeasured_AndIsNotTheSameStatementAsUnresolved()
    {
        var notAsked = ResolvedContentType.Of(null, NodeTypePath);
        // A process with no content-type registry MEASURED NOTHING, and reporting that as
        // "unresolved" would report a finding nobody made.
        Assert.Equal(ResolvedContentType.NotAsked, notAsked.Status);
        Assert.Contains("not measured", notAsked.Describe(), StringComparison.Ordinal);

        var unresolved = ResolvedContentType.Of(new MeshContentTypeRegistry(), NodeTypePath);
        Assert.Equal(ResolvedContentType.Unresolved, unresolved.Status);
        // The two must stay distinguishable: an absent instrument may not read as a clean result.
        Assert.NotEqual(notAsked.Status, unresolved.Status);
        // The consequence is what an operator needs to hear: the views of that node render blank.
        Assert.Contains("render empty", unresolved.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void AResolvedTypeNamesItsOwnAssemblyFileAndMvid()
    {
        var registry = new MeshContentTypeRegistry();
        registry.Register(typeof(Marker), NodeTypePath);

        var reading = ResolvedContentType.Of(registry, NodeTypePath);

        Assert.Equal(ResolvedContentType.Resolved, reading.Status);
        Assert.Equal(typeof(Marker).FullName, reading.TypeName);
        // The path reported is the LOADED assembly's own, not a store key off a record.
        Assert.Equal(typeof(Marker).Assembly.Location, reading.Assembly);
        // The mvid is minted by the compiler into the bytes, so it cannot be stale.
        Assert.Equal(
            typeof(Marker).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..8],
            reading.Mvid);
        Assert.False(reading.Collectible);
    }

    [Fact]
    public void TwoBuildsOfOneTypeNameReadDIFFERENTLY_whichIsTheWholePoint()
    {
        // 🚨 ASSERTED, never SKIPPED. A skip here would make the one case that can falsify this
        // reading silently vacuous, which is the failure mode this whole file is about.
        var file = typeof(Marker).Assembly.Location;
        Assert.False(string.IsNullOrEmpty(file),
            "this control re-loads the test assembly's own bytes, so it needs a file-backed load; "
            + "if that ever stops being true the control must go RED, not quietly pass");

        var defaultContext = new MeshContentTypeRegistry();
        defaultContext.Register(typeof(Marker), NodeTypePath);
        var shipped = ResolvedContentType.Of(defaultContext, NodeTypePath);

        var alc = new AssemblyLoadContext("resolved-content-type-control", isCollectible: true);
        try
        {
            Assembly copy;
            using (var stream = File.OpenRead(file))
                copy = alc.LoadFromStream(stream);
            var sameName = copy.GetType(typeof(Marker).FullName!, throwOnError: true)!;
            // The control only means anything if BOTH types answer to the same full name.
            Assert.Equal(typeof(Marker).FullName, sameName.FullName);
            Assert.NotSame(typeof(Marker), sameName);

            var collectibleRegistry = new MeshContentTypeRegistry();
            collectibleRegistry.Register(sameName, NodeTypePath);
            var compiled = ResolvedContentType.Of(collectibleRegistry, NodeTypePath);

            Assert.Equal(ResolvedContentType.Resolved, compiled.Status);
            // Same full name — so the NAME cannot be what tells these two apart.
            Assert.Equal(shipped.TypeName, compiled.TypeName);
            // A type in a collectible context is an in-process compile, not a module shipped with
            // the process — the distinction #3732 needed and nothing else on the reply carries.
            Assert.True(compiled.Collectible);
            Assert.False(shipped.Collectible);
            // An assembly loaded from a byte array has an EMPTY Location; reporting it as an
            // assembly at path "" would be worse than saying it has no file.
            Assert.Null(compiled.Assembly);
            Assert.NotNull(shipped.Assembly);
            Assert.Contains("collectible", compiled.Describe(), StringComparison.Ordinal);
            // If these were equal, the reading would be a function of the NodeType path rather
            // than of the bytes that answered it — which is exactly what #4158 asked to fix.
            Assert.NotEqual(shipped, compiled);
        }
        finally
        {
            alc.Unload();
        }
    }
}
