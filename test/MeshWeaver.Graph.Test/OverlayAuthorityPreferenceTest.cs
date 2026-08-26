using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit contract for <see cref="NodeTypeEnrichmentHelpers.PreferAuthoritative"/> — the decision that
/// stops an instance from being committed to the compile-progress overlay on the strength of a
/// latched mirror snapshot (#2409).
///
/// <para><b>The bug this pins.</b> Two halves of the overlay machinery consulted different
/// authorities. The self-heal watcher re-evaluated against STORAGE
/// (<c>AuthoritativeTypeRead</c>) — but only the BIND path decides what an activating instance
/// actually serves, and it read the mesh hub's mirror through <c>IMeshNodeStreamCache</c>. That
/// cache hands every consumer one process-wide <c>ReplaySubject(1)</c> per path, and when the
/// owner's sync stream goes SILENT (rather than faulting) the subject replays its last value
/// forever: fault-eviction needs a fault, and idle-eviction needs zero subscribers — which the
/// overlay's own long-lived watchers guarantee can never happen.</para>
///
/// <para>A latched <c>Compiling</c> snapshot therefore made the overlay branch DETERMINISTIC. The
/// self-heal correctly saw a usable build in storage and recycled the instance; re-enrichment read
/// the same latched snapshot and overlaid it again, five seconds later. Heal → recycle →
/// re-poison, spaced out to ten minutes by <c>OverlayHealBudget</c>. On memex, ten of twelve public
/// package covers served the Roslyn compile-progress screen to anonymous visitors for over an hour
/// after the type had compiled, and recycling could not clear it.</para>
/// </summary>
public class OverlayAuthorityPreferenceTest
{
    private const string NodeTypePath = "Store/Plugin";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Default);

    private static NodeTypeDefinition Def(CompilationStatus? status) => new()
    {
        Description = "overlay-authority probe",
        Configuration = "config => config",
        CompilationStatus = status,
        LatestAssemblyCollection = status == CompilationStatus.Ok ? "assemblies" : null,
        LatestAssemblyPath = status == CompilationStatus.Ok ? "Store/Plugin/1" : null,
    };

    /// <summary>The mirror's shape: content un-materialized, as it crosses a sync stream.</summary>
    private static MeshNode Mirror(CompilationStatus? status, long version) => new(NodeTypePath)
    {
        Version = version,
        Content = JsonSerializer.SerializeToElement(Def(status), Options)
    };

    /// <summary>A storage read: typed content, and NEVER a HubConfiguration — the field is
    /// <c>[JsonIgnore, NotMapped]</c> and every storage boundary strips it.</summary>
    private static MeshNode Stored(CompilationStatus? status, long version) => new(NodeTypePath)
    {
        Version = version,
        Content = Def(status)
    };

    [Fact]
    public void SettledStorageRead_WinsOverALatchedCompilingMirror()
    {
        // The exact prod shape: the mirror is frozen on the 16:09 compile, storage carries the
        // 16:26 one that finished.
        var latched = Mirror(CompilationStatus.Compiling, version: 12_370);
        var settled = Stored(CompilationStatus.Ok, version: 12_379);

        var chosen = NodeTypeEnrichmentHelpers.PreferAuthoritative(latched, settled);

        NodeTypeEnrichmentHelpers.IsCompileInFlight(chosen, Options).Should().BeFalse(
            "the instance must bind the build that actually exists, not paint a progress screen "
            + "about a compile that finished over an hour ago (#2409)");
        chosen.Version.Should().Be(12_379);
    }

    [Fact]
    public void StorageThatAgreesTheCompileIsInFlight_StillOverlays()
    {
        // The honest in-flight case must be untouched: a genuine compile still gets the progress
        // page, and it gets it against the version storage knows about.
        var mirror = Mirror(CompilationStatus.Compiling, version: 40);
        var stored = Stored(CompilationStatus.Pending, version: 41);

        var chosen = NodeTypeEnrichmentHelpers.PreferAuthoritative(mirror, stored);

        NodeTypeEnrichmentHelpers.IsCompileInFlight(chosen, Options).Should().BeTrue(
            "when both authorities agree a compile is running, the overlay is the right answer");
    }

    [Fact]
    public void NoStorageAnswer_KeepsTheStreamSnapshot()
    {
        // Absence of an authoritative answer is not evidence. A path storage has no row for
        // (a static-provider type, a test hub with no query core) must not be read as "settled".
        var mirror = Mirror(CompilationStatus.Compiling, version: 7);

        var chosen = NodeTypeEnrichmentHelpers.PreferAuthoritative(mirror, authoritative: null);

        chosen.Should().BeSameAs(mirror);
        NodeTypeEnrichmentHelpers.IsCompileInFlight(chosen, Options).Should().BeTrue();
    }

    [Fact]
    public void PreferringStorage_NeverStripsTheHubConfiguration()
    {
        // 🚨 HubConfiguration is [JsonIgnore, NotMapped]: its absence on a storage row is the
        // READ's blind spot, never information about the type. A static-provider NodeType's whole
        // configuration IS that delegate, so dropping it would bind the bare default chain and
        // lose every one of the type's areas — trading a stuck placeholder for an empty page.
        static MessageHubConfiguration Configure(MessageHubConfiguration c) => c;
        var mirror = Mirror(CompilationStatus.Compiling, version: 3) with
        {
            HubConfiguration = Configure
        };
        var stored = Stored(CompilationStatus.Ok, version: 4);
        stored.HubConfiguration.Should().BeNull("a storage read can never carry the delegate");

        var chosen = NodeTypeEnrichmentHelpers.PreferAuthoritative(mirror, stored);

        chosen.Version.Should().Be(4, "the storage read is still the authority on compile state");
        chosen.HubConfiguration.Should().NotBeNull(
            "the delegate the storage read could not see must be carried across");
    }
}
