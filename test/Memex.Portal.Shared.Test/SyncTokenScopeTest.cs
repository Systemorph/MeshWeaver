using System;
using System.Collections.Generic;
using System.Linq;
using Memex.Portal.Shared.Api;
using MeshWeaver.Mesh.Security;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins how a token-exchange request is intersected with the instance's sync licence — the pure
/// decision behind <c>POST /api/instances/token</c>'s scope.
///
/// <para>Written because the first implementation got the whole-source case wrong: it MATCHED
/// <c>Plugins/*</c> against the licence instead of EXPANDING it, so an instance licensed per-package
/// that asked for "everything I hold in this source" got an empty scope and a 403.</para>
/// </summary>
public class SyncTokenScopeTest
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static AuthenticatedInstance Caller(bool revoked = false, params PluginGrantEntry[] entries) =>
        new(new MeshWeaverInstance { InstanceId = "manufacturing-ci", KeyHash = "hash" },
            new PluginGrant { InstanceId = "manufacturing-ci", Entries = entries, IsRevoked = revoked });

    private static PluginGrantEntry Entry(string source, string package, DateTimeOffset? expires = null) =>
        new() { Source = source, PackageId = package, ExpiresAt = expires };

    private static List<string> Scope(AuthenticatedInstance caller, params string[] requested) =>
        InstanceTokenEndpoints.EffectiveScope(caller, requested.Length == 0 ? null : requested, Now).ToList();

    [Fact]
    public void NoRequest_YieldsEverythingLicensed()
    {
        var caller = Caller(false, Entry("Plugins", "Publish"), Entry("Plugins", "Store"));
        Assert.Equal(["Plugins/Publish", "Plugins/Store"], Scope(caller));
    }

    [Fact]
    public void AnExactRequestIsHonoured()
    {
        var caller = Caller(false, Entry("Plugins", "Publish"), Entry("Plugins", "Store"));
        Assert.Equal(["Plugins/Publish"], Scope(caller, "Plugins/Publish"));
    }

    [Fact]
    public void AWholeSourceRequest_EXPANDS_ToTheLicensedPackages()
    {
        // THE regression this test exists for: matching instead of expanding returned nothing, and
        // the endpoint answered 403 to a caller asking a perfectly reasonable question.
        var caller = Caller(false, Entry("Plugins", "Publish"), Entry("Plugins", "Store"));
        Assert.Equal(["Plugins/Publish", "Plugins/Store"], Scope(caller, "Plugins/*"));
    }

    [Fact]
    public void AWholeSourceRequest_ExpandsOnlyWithinThatSource()
    {
        var caller = Caller(false, Entry("Plugins", "Publish"), Entry("Education", "DataModeling"));
        Assert.Equal(["Plugins/Publish"], Scope(caller, "Plugins/*"));
    }

    [Fact]
    public void AWholeSourceLicenceStaysAWholeSourceScope()
    {
        var caller = Caller(false, Entry("Plugins", PluginGrantEntry.AllPackages));
        Assert.Equal(["Plugins/*"], Scope(caller, "Plugins/*"));
        // And an exact request under a whole-source licence narrows to that package.
        Assert.Equal(["Plugins/Publish"], Scope(caller, "Plugins/Publish"));
    }

    [Fact]
    public void AnUnlicensedRequestIsDropped_NotRefused()
    {
        // A token can only narrow, so an over-broad request is a stale caller rather than an attack.
        var caller = Caller(false, Entry("Plugins", "Publish"));
        Assert.Equal(["Plugins/Publish"], Scope(caller, "Plugins/Publish", "Education/DataModeling"));
    }

    [Fact]
    public void RequestingOnlyUnlicensedThings_YieldsNothing_SoTheEndpointCanSayWhy()
    {
        var caller = Caller(false, Entry("Plugins", "Publish"));
        Assert.Empty(Scope(caller, "Education/DataModeling"));
    }

    [Fact]
    public void AnExpiredEntryIsNotOffered()
    {
        // Minting against an ended licence would hand back a token that fails on every call.
        var caller = Caller(false, Entry("Plugins", "Publish", Now.AddDays(-1)), Entry("Plugins", "Store"));
        Assert.Equal(["Plugins/Store"], Scope(caller));
        Assert.Empty(Scope(caller, "Plugins/Publish"));
    }

    [Fact]
    public void AnExpiredEntryIsNotOfferedByAWildcardEither()
    {
        var caller = Caller(false, Entry("Plugins", "Publish", Now.AddDays(-1)));
        Assert.Empty(Scope(caller, "Plugins/*"));
    }

    [Fact]
    public void ARevokedGrantOffersNothing()
    {
        var caller = Caller(true, Entry("Plugins", "Publish"));
        Assert.Empty(Scope(caller));
        Assert.Empty(Scope(caller, "Plugins/Publish"));
        Assert.Empty(Scope(caller, "Plugins/*"));
    }

    [Fact]
    public void MalformedRequestEntriesAreIgnored_NeverThrown()
    {
        var caller = Caller(false, Entry("Plugins", "Publish"));
        Assert.Equal(["Plugins/Publish"], Scope(caller, "", "   ", "/", "Plugins/Publish"));
    }

    [Fact]
    public void DuplicateRequestsCollapse()
    {
        var caller = Caller(false, Entry("Plugins", "Publish"));
        Assert.Equal(["Plugins/Publish"], Scope(caller, "Plugins/Publish", "Plugins/Publish", "Plugins/*"));
    }

    [Fact]
    public void SourceMatchingIsCaseInsensitive_AsItIsInAGrant()
    {
        var caller = Caller(false, Entry("Plugins", "Publish"));
        Assert.Equal(["Plugins/Publish"], Scope(caller, "plugins/Publish"));
        Assert.Equal(["Plugins/Publish"], Scope(caller, "PLUGINS/*"));
    }
}
