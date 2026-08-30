using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;

using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Auth.Test;

/// <summary>
/// Issue #2695 — "I could not find out" must never be reported, or REMEMBERED, as "this key is
/// unknown".
///
/// <para>Three point reads stand between a bearer key and its instance. A transient failure of any
/// of them used to become a <c>null</c> — rendered as <c>401 "A registered instance key is
/// required"</c> — and then <b>cached for a full minute</b>, so one slow read on one pod made a
/// valid key unknown to that pod for sixty seconds. MeshWeaver.Crm's gate (run 33269921011) hit it
/// in both jobs and read the 401 as "this instance needs a whole-source grant", while the grant sat
/// unchanged; a re-run minutes later passed with nothing altered anywhere.</para>
///
/// <para>These drive the authenticator's read seam directly, so what is pinned is the
/// CLASSIFICATION and the CACHE rule rather than "an error appears somewhere". The definitive legs
/// are pinned alongside, so the fix cannot over-reach into "everything is retryable" — an unknown
/// key must still be a fast, cacheable, definitive no.</para>
/// </summary>
public class InstanceKeyUnavailableNotUnknownTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Key = "mwi_0123456789abcdef0123456789abcdef";

    /// <summary>Every read answers Unavailable — the shape of a stalled or faulted partition.</summary>
    /// <summary>A REAL authenticator on the real mesh hub — only its node READ is driven, so the
    /// System impersonation, the hashing and the cache are the production ones.</summary>
    private InstanceRegistryAuthenticator Authenticator(
        Func<string, IObservable<NodeReadOutcome>> read, List<string>? seen = null) =>
        new(Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<InstanceRegistryAuthenticator>>())
        {
            ReadOverride = path =>
            {
                seen?.Add(path);
                return read(path);
            },
        };

    private static NodeReadOutcome Unavailable() =>
        new() { Status = NodeReadStatus.Unavailable, Failure = new TimeoutException("read stalled") };

    private static NodeReadOutcome Absent() => new() { Status = NodeReadStatus.Absent };

    /// <summary>
    /// SHOULD-FAIL-IF: an unreadable index is reported as an authentication verdict. That is the
    /// defect — the endpoint then tells a valid caller its key is unknown.
    /// </summary>
    [Fact]
    public async Task AnUnreadableIndex_IsUnavailable_NotUnknownKey()
    {
        var outcome = await Authenticator(_ => Observable.Return(Unavailable()))
            .AuthenticateOutcome($"Bearer {Key}").FirstAsync().Await();

        outcome.IsUnavailable.Should().BeTrue(
            "a read that reached no verdict establishes NOTHING about the presented key");
        outcome.Instance.Should().BeNull();
        outcome.UnavailableReason.Should().Contain("read stalled",
            "the cause travels to the endpoint, which logs it beside the 503");
    }

    /// <summary>
    /// SHOULD-FAIL-IF: the unavailable result is cached. This is the part that turned one slow read
    /// into a MINUTE of 401s — and it is invisible in a single-request test, which is why the second
    /// call here is the assertion.
    /// </summary>
    [Fact]
    public async Task AnUnavailableResolution_IsNeverCached()
    {
        var attempts = 0;
        var authenticator = Authenticator(_ =>
        {
            attempts++;
            return Observable.Return(Unavailable());
        });

        await authenticator.AuthenticateOutcome($"Bearer {Key}").FirstAsync().Await();
        var firstRoundAttempts = attempts;
        await authenticator.AuthenticateOutcome($"Bearer {Key}").FirstAsync().Await();

        attempts.Should().BeGreaterThan(firstRoundAttempts,
            "the second call must RE-READ the mesh: a fault is not a fact, and remembering it is "
            + "what locked a valid instance out for the whole cache duration");
    }

    /// <summary>
    /// SHOULD-FAIL-IF: the fix over-reaches. A key that genuinely resolves to nothing is a verdict —
    /// it must be reported as a denial (401, not 503) and it must still be cached, or every
    /// unauthenticated request re-reads three nodes.
    /// </summary>
    [Fact]
    public async Task AnAbsentIndex_IsADefinitiveNo_AndIsCached()
    {
        var attempts = 0;
        var authenticator = Authenticator(_ =>
        {
            attempts++;
            return Observable.Return(Absent());
        });

        var first = await authenticator.AuthenticateOutcome($"Bearer {Key}").FirstAsync().Await();
        first.IsUnavailable.Should().BeFalse("an absent index IS an answer: this key is unknown");
        first.Instance.Should().BeNull();

        var after = attempts;
        var second = await authenticator.AuthenticateOutcome($"Bearer {Key}").FirstAsync().Await();
        second.IsUnavailable.Should().BeFalse();
        attempts.Should().Be(after, "a definitive negative is cached — briefly, but cached");
    }

    /// <summary>
    /// SHOULD-FAIL-IF: only the FIRST read is classified. The grant leg is the easiest to miss and
    /// the worst to get wrong: an unreadable grant authenticates the caller and then refuses every
    /// package it asks for — a 403 nobody can act on — whereas an ABSENT grant is the normal state
    /// of a freshly registered instance and must stay a clean authentication.
    ///
    /// <para>This drives all three legs for real: a matching index, a matching instance, and then a
    /// grant read that either stalls or is absent.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]   // grant unreadable  → UNAVAILABLE, uncached, 503
    [InlineData(false)]  // grant absent      → authenticated with an empty grant, as designed
    public async Task TheGrantLegIsClassifiedToo(bool grantStalls)
    {
        var hash = InstanceKeys.Hash(Key);
        const string instancePath = "owner/MeshWeaverInstance/inst-a";
        var grantPath = MeshWeaverInstanceNodeType.GrantPath("inst-a");

        MeshNode Node(string path, object content) =>
            MeshNode.FromPath(path) with { Content = content };

        var seen = new List<string>();
        var authenticator = Authenticator(path =>
        {
            if (path == grantPath)
                return Observable.Return(grantStalls ? Unavailable() : Absent());
            if (path == instancePath)
                return Observable.Return(new NodeReadOutcome
                {
                    Status = NodeReadStatus.Present,
                    Node = Node(instancePath, new MeshWeaverInstance
                    {
                        InstanceId = "inst-a", KeyHash = hash, DisplayName = "A",
                    }),
                });
            // the index
            return Observable.Return(new NodeReadOutcome
            {
                Status = NodeReadStatus.Present,
                Node = Node(path, new MeshWeaverInstanceIndex
                {
                    KeyHash = hash, InstancePath = instancePath, InstanceId = "inst-a",
                }),
            });
        }, seen);

        var outcome = await authenticator.AuthenticateOutcome($"Bearer {Key}").FirstAsync().Await();

        seen.Should().Contain(grantPath, "all three legs must actually be read");
        if (grantStalls)
        {
            outcome.IsUnavailable.Should().BeTrue(
                "an unreadable GRANT establishes nothing either — authenticating and then denying "
                + "every package is a verdict the read never reached");
            outcome.UnavailableReason.Should().Contain(grantPath, "the failing leg is named");
        }
        else
        {
            outcome.IsUnavailable.Should().BeFalse();
            outcome.Instance.Should().NotBeNull("an ABSENT grant is the normal freshly-registered state");
            outcome.Instance!.Grant.Allows("Plugins", "Store").Should().BeFalse(
                "…and it authorizes nothing");
        }
    }

    /// <summary>The negative cache is deliberately far shorter than the positive one: nobody polls
    /// a key that does not work, and a minute of lockout after a fresh registration buys nothing.</summary>
    [Fact]
    public void NegativeCache_IsShorterThanPositive()
    {
        InstanceRegistryAuthenticator.NegativeCacheDuration.Should()
            .BeLessThan(InstanceRegistryAuthenticator.CacheDuration);
        InstanceRegistryAuthenticator.RetryAfterSeconds.Should().BeGreaterThan(0,
            "the 503 advertises a retry budget, like the identity leg's");
    }
}
