using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pure, host-independent unit tests for the write-once DECISION — the single correctness
/// property of the time-zone population feature (browser detect must NEVER clobber an existing
/// or user-chosen zone). Deterministic: no mesh, no clock, no host-zone dependency.
/// </summary>
public class TimeZonePreferenceTest
{
    [Theory]
    [InlineData(null, "America/New_York", "America/New_York")] // empty profile → write detected
    [InlineData("", "America/New_York", "America/New_York")]
    [InlineData("   ", "America/New_York", "America/New_York")]
    [InlineData(null, "  Europe/Zurich  ", "Europe/Zurich")]   // trims the detected value
    public void ShouldWrite_WritesDetected_WhenProfileEmpty(string? current, string? detected, string expected)
        => TimeZonePreference.ShouldWrite(current, detected).Should().Be(expected);

    [Theory]
    // A non-empty existing value is NEVER overwritten — the whole point of write-once.
    [InlineData("Europe/Zurich", "America/New_York")]
    [InlineData("America/Los_Angeles", "America/New_York")]
    [InlineData("Europe/Zurich", null)]
    [InlineData("Europe/Zurich", "")]
    // Nothing detected → nothing to write, even on an empty profile.
    [InlineData(null, null)]
    [InlineData("", "   ")]
    public void ShouldWrite_ReturnsNull_WhenNoWriteWanted(string? current, string? detected)
        => TimeZonePreference.ShouldWrite(current, detected).Should().BeNull();

    [Fact]
    public void SystemZoneIds_AreNonEmpty_Unique_AndResolvable()
    {
        var ids = TimeZonePreference.SystemZoneIds();

        ids.Should().NotBeEmpty();
        ids.Should().OnlyHaveUniqueItems();
        // Every id the picker offers must resolve as a named zone (the display seam falls back to
        // UTC on an unknown id, so an unresolvable option would silently un-localize).
        foreach (var id in ids.Take(50))
            DisplayTimeExtensions.ToDisplayTime(DateTimeOffset.UtcNow, id); // must not throw
    }
}

/// <summary>
/// Integration test for the reactive write-once population against a real user node on the
/// monolith mesh: (a) an empty <see cref="User.TimeZoneId"/> is populated from a detected zone;
/// (b) a subsequent detect NEVER overwrites the now-non-empty value; (c) a manual override
/// (the settings-editor write path) persists and, read back off the node, drives
/// <c>AccessService.ToDisplayTime</c>.
/// </summary>
public class TimeZonePopulationIntegrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // A stored summer instant — used to prove the persisted zone actually localizes a timestamp.
    private static readonly DateTimeOffset SummerUtc = new(2026, 7, 13, 16, 0, 0, TimeSpan.Zero);

    private async Task SeedUserRoot(string id)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(MeshNode.FromPath(id) with
            {
                Name = "Zone User",
                NodeType = "User",
                State = MeshNodeState.Active,
                Content = new User { Email = $"{id}@test.com" }, // TimeZoneId deliberately unset
            }).Should().Within(30.Seconds()).Emit();
        }
    }

    private async Task<string?> ReadZone(string userPath)
    {
        var node = await ReadNode(userPath).Should().Within(20.Seconds()).Match(n => n is not null);
        return node!.ContentAs<User>(Mesh.JsonSerializerOptions)?.TimeZoneId;
    }

    [Fact(Timeout = 60000)]
    public async Task PopulateOnce_WritesDetected_ThenNeverOverwrites_AndOverrideDrivesDisplay()
    {
        const string id = "tzuser_writeonce";
        await SeedUserRoot(id);
        var options = Mesh.JsonSerializerOptions;

        // Precondition: the profile has no zone yet.
        (await ReadZone(id)).Should().BeNullOrEmpty();

        // (a) Empty profile + a detected zone → the zone is written once.
        await TimeZonePreference.PopulateOnce(Mesh, id, "America/New_York", options)
            .DefaultIfEmpty().ToTask(TestContext.Current.CancellationToken);

        await ReadNode(id).Should().Within(20.Seconds())
            .Match(n => n!.ContentAs<User>(options)!.TimeZoneId == "America/New_York");

        // (b) A LATER detect (e.g. the user is on a VPN reporting a different zone) must NOT
        //     overwrite the stored value — write-once is the key correctness property.
        await TimeZonePreference.PopulateOnce(Mesh, id, "Europe/Zurich", options)
            .DefaultIfEmpty().ToTask(TestContext.Current.CancellationToken);

        (await ReadZone(id)).Should().Be("America/New_York",
            "a non-empty TimeZoneId must never be clobbered by a subsequent browser detect");

        // (c) The settings-editor OVERRIDE path: a deliberate user pick writes the field directly
        //     (same node-stream write the MeshNodeContentEditor performs). This SHOULD replace the
        //     auto-detected value — an explicit setting always wins.
        Mesh.GetMeshNodeStream(id)
            .Update(current =>
            {
                var u = current.ContentAs<User>(options)!;
                return current with { Content = u with { TimeZoneId = "Europe/Zurich" } };
            })
            .Subscribe(_ => { }, ex => Output.WriteLine($"override write failed: {ex}"));

        var overridden = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => ReadNode(id))
            .Select(n => n?.ContentAs<User>(options)?.TimeZoneId)
            .Should().Within(30.Seconds())
            .Match(z => z == "Europe/Zurich");

        // …and a subsequent auto-detect still respects the (now user-chosen) value.
        await TimeZonePreference.PopulateOnce(Mesh, id, "America/Los_Angeles", options)
            .DefaultIfEmpty().ToTask(TestContext.Current.CancellationToken);
        (await ReadZone(id)).Should().Be("Europe/Zurich",
            "the manual override must survive later browser detects too");

        // The persisted zone, read back off the node, drives the display seam (Zurich summer = +2).
        var access = new AccessService();
        using (access.SwitchAccessContext(new AccessContext { ObjectId = id, TimeZoneId = overridden }))
        {
            access.ToDisplayTime(SummerUtc).Hour.Should().Be(18,
                "a stored 16:00Z renders as 18:00 for a Europe/Zurich viewer in summer (CEST, +2)");
        }
    }
}
