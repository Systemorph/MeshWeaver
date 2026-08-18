using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// WHO a timed publish goes out as — the gate that keeps the scheduled path answering to the same
/// rules as the Publish button.
///
/// <para>🚨 <c>LinkedInPublishService</c> picks the credential from the POST's own
/// <c>authorPath</c>, and its two access gates are the only thing stopping a caller using a profile
/// that is not theirs. The runner impersonates system, so a timed publish that inherited that would
/// pass both gates unconditionally: anyone able to EDIT a post could point it at another member's
/// profile and have the timer post as them. These tests pin the refusals that close it.</para>
/// </summary>
public class ScheduledPublishIdentityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private ScheduledSocialPublishHandler Handler() =>
        new(Mesh,
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh.ServiceProvider.GetRequiredService<IHttpClientFactory>(),
            Mesh.ServiceProvider.GetRequiredService<AccessService>());

    private static EventSubscription Timer(string? createdBy) => new()
    {
        TriggerType = EventTriggerType.Timer,
        FireAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        ContinuationType = EventContinuationType.PublishSocialPost,
        TargetPath = "TestData/some-post",
        CreatedBy = createdBy,
    };

    private async Task<string> RefusalFor(string? createdBy)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Handler().Execute(Timer(createdBy), string.Empty).FirstAsync().ToTask());
        return error.Message;
    }

    /// <summary>No identity at all — nothing to gate against, so it must not publish.</summary>
    [Fact(Timeout = 60000)]
    public async Task NoScheduler_IsRefused() =>
        Assert.Contains("names no CreatedBy", await RefusalFor(null));

    /// <summary>
    /// 🚨 The system identity is refused even though it is PRESENT. The watcher takes CreatedBy from
    /// the post's lastModifiedBy, which is the system whenever the node was last written by a
    /// GitSync, an import or a migration — so a check for "blank" alone would have left the bypass
    /// wide open for every system-written post, which is most of them on a synced space.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SystemScheduler_IsRefused_EvenThoughItIsPresent() =>
        Assert.Contains("system identity", await RefusalFor(WellKnownUsers.System));

    /// <summary>A hub principal is address-shaped and is never a user; same bypass, same refusal.</summary>
    [Fact(Timeout = 60000)]
    public async Task HubScheduler_IsRefused() =>
        Assert.Contains("hub address", await RefusalFor("sync/eeNyrliHwkuBmH"));
}
