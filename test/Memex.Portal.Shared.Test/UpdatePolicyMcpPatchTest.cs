#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #2469: an MCP <c>patch</c> to <c>Admin/UpdatePolicy</c> reported success
/// (<c>"Patched: Admin/UpdatePolicy"</c>, no version-transition suffix) while the durable node
/// stayed on its old <see cref="UpdatePolicyContent.Policy"/> — the operator's auto-update
/// kill-switch never took effect. Reproduces the exact operator sequence against a REAL mesh:
/// seed the node at the PLATFORM DEFAULT policy (<see cref="UpdatePolicyKind.Continuous"/> —
/// also the enum's own default value, so it is OMITTED from JSON under this hub's
/// <c>DefaultIgnoreCondition = WhenWritingDefault</c>), then run the exact MCP <c>patch</c> call
/// an operator would issue to freeze auto-update, while the self-update poller concurrently
/// writes its own bookkeeping fields (<c>checkedAt</c> / <c>latestAvailableTag</c>) on the SAME
/// node — precisely what <see cref="Memex.Portal.Shared.SelfUpdate.SelfUpdateHostedService.RecordAvailable"/>
/// does continuously in production.
/// </summary>
public class UpdatePolicyMcpPatchTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddUpdatePolicyType();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>
    /// The plain case: no concurrent writer at all. An admin patches
    /// <c>Admin/UpdatePolicy</c>'s <c>policy</c> field away from its (platform- and enum-)
    /// default. The tool's own reply must be trustworthy: if it says "Patched", the field must
    /// actually have changed.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Patch_SetsPolicyAwayFromDefault_ActuallyLands()
    {
        await Seed();

        var result = await new MeshOperations(Mesh)
            .Patch(UpdatePolicyNodeType.NodePath, """{"content":{"policy":"None"}}""")
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);

        Output.WriteLine($"Patch tool returned: {result}");
        result.Should().NotContain("Error:", "a real, applicable field change must not be refused");

        var content = await ReadContentOnce();
        content.Policy.Should().Be(UpdatePolicyKind.None,
            "the tool reported success, so the write must actually have landed");
    }

    /// <summary>
    /// The production shape: the self-update poller keeps ticking <c>checkedAt</c> /
    /// <c>latestAvailableTag</c> on the SAME node (as System) while the operator's patch is in
    /// flight — exactly the contention the issue names ("two fields, different writers, different
    /// lifetimes, contending on one row").
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Patch_SetsPolicyAwayFromDefault_ConcurrentWithPollerBookkeeping_ActuallyLands()
    {
        await Seed();

        // Fire a handful of poller-shaped bookkeeping writes (as System) racing the operator's
        // patch — mirrors SelfUpdateHostedService.RecordAvailable's write shape exactly.
        var pollerTicks = Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                await RecordAvailable($"3.0.0-ci.{i}");
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        var result = await new MeshOperations(Mesh)
            .Patch(UpdatePolicyNodeType.NodePath, """{"content":{"policy":"None"}}""")
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);

        await pollerTicks;

        Output.WriteLine($"Patch tool returned: {result}");
        result.Should().NotContain("Error:", "a real, applicable field change must not be refused");

        var content = await ReadContentOnce();
        content.Policy.Should().Be(UpdatePolicyKind.None,
            "the operator's write must survive concurrent poller bookkeeping on unrelated fields");
    }

    /// <summary>
    /// A companion invariant check for #2469: <c>Patch</c>'s success string must be an honest
    /// signal — if it does not say "Error:", the caller's OWN field must actually have changed.
    /// Runs the operator's patch against a heavy REAL-concurrency write storm on the exact same
    /// leaf (<c>policy</c>, seeded away from its enum default so base values are actually carried
    /// and a genuine Conflict can fire) — this fast in-memory mesh usually resolves the race
    /// within <c>UpdateRemote</c>'s 2s response bound, so it does not reliably discriminate
    /// old/new code by itself (see <c>PatchLandedWriteCheckTest</c> for the deterministic,
    /// timing-independent pin of the actual defect — the version-only confirmation this fix
    /// replaces), but it does prove the fix holds under real contention, not just the quiet,
    /// uncontended case the other tests in this file cover.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task Patch_SuccessString_IsHonest_UnderConcurrentContentionOnTheSameField()
    {
        // Seeded away from the enum default (Continuous) so "policy" is NOT omitted from JSON —
        // ExtractBaseValues carries a real base for it, so a concurrent write to the SAME leaf
        // produces a genuine Conflict refusal instead of the no-base "apply blindly" fallback.
        await Seed(UpdatePolicyKind.Stable);

        // A heavy concurrent storm on the SAME leaf ("policy", toggled between two OTHER values)
        // — real concurrency (Task.WhenAll, not sequential awaits) so it genuinely backs up the
        // owning hub's single-threaded action block. Started slightly ahead of, and kept running
        // through, the operator's own patch call so that call's PatchDataRequest is likely queued
        // behind a real backlog when the owner finally answers it.
        using var stormCts = new CancellationTokenSource();
        var storm = Task.WhenAll(Enumerable.Range(0, StormSize).Select(i => Task.Run(async () =>
        {
            try
            {
                await ToggleStormWriter(i % 2 == 0 ? UpdatePolicyKind.Continuous : UpdatePolicyKind.Stable);
            }
            catch (OperationCanceledException) { /* storm cancelled after the patch settles */ }
            catch (Exception ex)
            {
                // Concurrent conflicts are EXPECTED and self-heal via the framework's own
                // re-enqueue; only log so a genuine infra fault is still visible.
                Output.WriteLine($"[storm] writer {i} faulted: {ex.GetType().Name}: {ex.Message}");
            }
        }, stormCts.Token)));

        await Task.Delay(50, TestContext.Current.CancellationToken); // let the storm get ahead

        var result = await new MeshOperations(Mesh)
            .Patch(UpdatePolicyNodeType.NodePath, """{"content":{"policy":"None"}}""")
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);

        // Check the claim AT THE INSTANT it was made — before the storm is torn down and its
        // remaining in-flight writes settle, which would let plenty of real time pass and give a
        // premature "Patched:" every chance to become true after the fact instead of proving it
        // was true when said.
        var content = await ReadContentOnce();

        await stormCts.CancelAsync();
        await storm;

        Output.WriteLine($"Patch tool returned: {result}");

        if (!result.Contains("Error:", StringComparison.Ordinal))
        {
            content.Policy.Should().Be(UpdatePolicyKind.None,
                "the tool did not report an error, so the caller's OWN field must actually have "
                + $"landed — instead the tool said '{result}' while policy was '{content.Policy}' "
                + "at that instant");
        }
    }

    // ── helpers ──

    /// <summary>Concurrent writers contending on the SAME leaf in the storm test — large enough
    /// to genuinely back up the owning hub's single-threaded action block past the 2s
    /// owner-response bound.</summary>
    private const int StormSize = 250;

    private Task Seed(UpdatePolicyKind policy = UpdatePolicyKind.Continuous)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            // The default overload seeds at the PLATFORM default AND the enum's own default
            // (Continuous = 0) — deliberately, so it is omitted from JSON under
            // DefaultIgnoreCondition = WhenWritingDefault, exactly like the real
            // Admin/UpdatePolicy node on a fresh install.
            Content = new UpdatePolicyContent { Policy = policy },
        };
        return Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return meshService.CreateNode(node).Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);
    }

    /// <summary>One storm writer: flips <c>policy</c> to <paramref name="value"/> — the SAME leaf
    /// the operator's patch targets, so a genuine base/live conflict is possible on either side.</summary>
    private Task ToggleStormWriter(UpdatePolicyKind value)
    {
        var jsonOptions = Mesh.JsonSerializerOptions;
        return Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return Mesh.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                        .Update(node =>
                        {
                            var cur = UpdatePolicyNodeType.ParseContent(node.Content, jsonOptions);
                            return node with { Content = cur with { Policy = value } };
                        })
                        .Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);
    }

    /// <summary>Same write shape as <c>SelfUpdateHostedService.RecordAvailable</c> — touches only
    /// the poller's bookkeeping fields, preserves Policy.</summary>
    private Task RecordAvailable(string tag)
    {
        var jsonOptions = Mesh.JsonSerializerOptions;
        return Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return Mesh.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                        .Update(node =>
                        {
                            var cur = UpdatePolicyNodeType.ParseContent(node.Content, jsonOptions);
                            return node with
                            {
                                Content = cur with { LatestAvailableTag = tag, CheckedAt = DateTimeOffset.UtcNow },
                            };
                        })
                        .Subscribe(observer);
            })
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);
    }

    /// <summary>A single authoritative read off the shared stream handle — the same primitive a
    /// follow-up MCP <c>get</c> uses.</summary>
    private Task<UpdatePolicyContent> ReadContentOnce() =>
        Observable.Create<MeshNode>(observer =>
            {
                using (Access.ImpersonateAsSystem())
                    return Mesh.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                        .Where(node => node is not null)
                        .Subscribe(observer);
            })
            .Select(node => UpdatePolicyNodeType.Parse(node, Mesh.JsonSerializerOptions))
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);
}
