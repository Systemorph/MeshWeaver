using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Pins the <b>activity owner credential model</b> (the activity mirror of the
/// thread-owner model): every activity-execution operation (create / progress /
/// complete) for a USER-triggered activity must run under the activity's OWNER
/// (<see cref="MeshNode.CreatedBy"/>) — re-established AT THE SUBSCRIBE via
/// <see cref="AccessContextScope.FromNode(MeshNode, AccessService, Microsoft.Extensions.Logging.ILogger, bool)"/>
/// — NOT under whatever ambient identity happens to be on the deferred
/// scheduler hop where the progress / terminal write actually fires.
///
/// <para><b>The defect this pins.</b> <see cref="ActivityRunner.RunActivity"/>
/// creates the activity node under the caller's identity (CreateNode captures
/// it synchronously), but the <c>command</c> runs its real work on the IoPool /
/// behind reactive scheduler hops, and the <c>Append</c> / <c>Finish</c> writes
/// fire on those hops where the originating AsyncLocal AccessContext has
/// cleared. Without re-establishing the owner at the subscribe, those writes
/// post context-null → the owning partition's RLS denies → the activity never
/// reaches a terminal Status (it hangs <c>Running</c> forever) and progress
/// lines never land.</para>
///
/// <para>The integration-test base masks this in the happy path because
/// <c>DevLogin</c> sets a PERSISTENT circuit-context fallback that survives the
/// thread hop. These tests deliberately clear that fallback and drive identity
/// purely through the per-delivery AsyncLocal <see cref="AccessService.Context"/>
/// — exactly the production shape (a background hub thread is never a Blazor
/// circuit) — so the missing-owner write is observable.</para>
/// </summary>
public class ActivityOwnerCredentialTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Security-on base (RLS enforced) — the partition gates activity writes.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => ConfigureMeshBase(builder);

    private const string Owner = "activity-owner";
    private const string NonOwner = "activity-intruder";

    protected override async Task SetupAccessRightsAsync()
    {
        // Grant the OWNER Editor (⇒ Update) on TestPartition so they may write
        // satellites under it; the NON-OWNER gets NO grant. Seed under System
        // (the documented identity for setup writes on a static-seeded
        // partition — bypasses the partition write guard's cold-provision race).
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        using (accessService.ImpersonateAsSystem())
        {
            await meshService.CreateNode(
                AssignmentNodeFactory.UserRole(Owner, "Editor", TestPartition))
                .Should().Emit();
        }
        await Mesh.GetEffectivePermissions(TestPartition, Owner)
            .Should().Match(p => p.HasFlag(Permission.Update));
    }

    /// <summary>
    /// The owner-model proof. The activity is created + run under the owner's
    /// AsyncLocal identity, but the command body — and therefore the
    /// <c>Append</c> / <c>Finish</c> writes that fire after it — runs on a
    /// thread that carries a DIFFERENT ("leaked") ambient identity — the
    /// production scheduler-hop shape, where the originating AsyncLocal has been
    /// replaced by whatever ran last on that pooled thread. The activity's
    /// progress + terminal writes must be attributed to the OWNER, never to the
    /// leaked identity, because the runner re-establishes the activity owner via
    /// <c>FromNode</c> + per-write <c>SwitchAccessContext</c>.
    /// <para>The deterministic discriminator is the write attribution
    /// (<see cref="MeshNode.LastModifiedBy"/>): the cross-hub write stamps it
    /// from the AccessContext captured AT THE <c>.Update()</c> invocation
    /// (MeshNodeStreamExtensions: <c>LastModifiedBy = capturedContextAtEntry.ObjectId</c>).
    /// WITHOUT the fix the Append/Finish writes fire under the leaked identity →
    /// <c>LastModifiedBy = leaked-intruder</c> (a wrong-author / identity-confusion
    /// audit defect, and an RLS-bypass on backends that gate the write); WITH the
    /// fix they are re-stamped to the owner → <c>LastModifiedBy = owner</c>.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task UserActivity_ProgressAndTerminalWrites_AttributedToOwner_NotLeakedAmbient()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        // 🚨 Drop the DevLogin persistent circuit fallback so it cannot mask the
        // defect: identity now flows ONLY through the per-delivery AsyncLocal,
        // exactly as it does on a production background hub thread.
        accessService.ClearPersistentCircuitContext();
        accessService.SetCircuitContext(null);
        accessService.SetContext(new AccessContext { ObjectId = Owner, Name = Owner });

        string? activityPath = null;

        // The command simulates the real producers (GitHub I/O / indexing): its
        // body runs OFF the originating context. We hop to a fresh task and set a
        // DIFFERENT identity (the "leaked intruder") on that thread — modelling a
        // pooled thread whose AsyncLocal carries someone else's identity — then
        // append a progress line FROM there. The append + the runner's terminal
        // Finish must be attributed to the OWNER, not the intruder.
        var run = Mesh.RunActivity(TestPartition, ActivityCategory.Unknown, "Owner-scoped op",
            ctx => Observable.Create<Unit>(o =>
            {
                _ = Task.Run(() =>
                {
                    accessService.SetContext(new AccessContext { ObjectId = NonOwner, Name = NonOwner });
                    ctx.Log("progress-from-foreign-context");
                    o.OnNext(Unit.Default);
                    o.OnCompleted();
                });
                return () => { };
            }),
            onActivityCreated: p => activityPath = p);

        using var sub = run.Subscribe(_ => { }, _ => { });

        var path = await WaitFor(() => activityPath);
        var log = await WaitForActivity(path, l => l.Status != ActivityStatus.Running);

        Assert.Equal(ActivityStatus.Succeeded, log.Status);
        Assert.Contains(log.Messages, m => m.Message.Contains("progress-from-foreign-context"));

        // The activity node is owned by the owner AND every producer write that
        // followed (Append + terminal Finish) is attributed to the owner — NOT to
        // the leaked intruder identity that was ambient on the hop thread.
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(10.Seconds()).ToTask();
        Assert.Equal(Owner, node!.CreatedBy);
        Assert.NotEqual(NonOwner, node.LastModifiedBy);
        Assert.Equal(Owner, node.LastModifiedBy);

        accessService.SetContext(null);
    }

    /// <summary>
    /// The RLS pin: a NON-owner with no Update on the partition is DENIED writing
    /// the activity. Proves the owner-model isn't a free-for-all — the satellite
    /// access rule delegates to the partition and only a principal who can Update
    /// the partition (the owner, here) may operate the activity. A wrong fix that
    /// ran every activity write as System would defeat this gate.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task NonOwner_CannotWriteAnothersActivity_RlsDenied()
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.GetWorkspace();
        accessService.ClearPersistentCircuitContext();
        accessService.SetCircuitContext(null);

        // Owner creates an activity node under the partition.
        var id = $"rls-{Guid.NewGuid():N}"[..12];
        var activityPath = $"{TestPartition}/_Activity/{id}";
        accessService.SetContext(new AccessContext { ObjectId = Owner, Name = Owner });
        await meshService.CreateNode(new MeshNode(id, $"{TestPartition}/_Activity")
        {
            NodeType = "Activity",
            Name = "Owner activity",
            State = MeshNodeState.Active,
            MainNode = TestPartition,
            Content = new ActivityLog(ActivityCategory.Unknown)
            {
                Id = id,
                HubPath = activityPath,
                Status = ActivityStatus.Running,
            },
        }).Should().Emit();

        // Sanity: the non-owner genuinely lacks Update on the partition (the
        // satellite rule delegates the activity's Update to the MainNode); the
        // OWNER does have it.
        var nonOwnerPerm = await Mesh.GetEffectivePermissions(TestPartition, NonOwner)
            .FirstAsync().Timeout(10.Seconds()).ToTask();
        Assert.False(nonOwnerPerm.HasFlag(Permission.Update),
            "the non-owner must not have Update on the partition for this RLS test to be meaningful");
        var ownerPerm = await Mesh.GetEffectivePermissions(TestPartition, Owner)
            .FirstAsync().Timeout(10.Seconds()).ToTask();
        Assert.True(ownerPerm.HasFlag(Permission.Update),
            "the owner must have Update on the partition (positive-control precondition)");

        // POSITIVE CONTROL: the OWNER's write to the activity DOES land. This
        // proves the write path is live and the gate is identity-based — not a
        // universal no-op that would make the negative assertion below vacuous.
        await workspace.GetMeshNodeStream(activityPath).Update(node =>
        {
            if (node.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions, null) is not { } log)
                return node;
            return node with
            {
                Content = log with
                {
                    Messages = log.Messages.Add(new LogMessage("OWNER-OK", Microsoft.Extensions.Logging.LogLevel.Information))
                }
            };
        }).FirstAsync().Timeout(10.Seconds()).ToTask();
        await workspace.GetMeshNodeStream(activityPath)
            .Select(n => n?.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions, null))
            .Where(l => l is not null && l.Messages.Any(m => m.Message == "OWNER-OK"))
            .FirstAsync().Timeout(10.Seconds()).ToTask();

        // The non-owner attempts a write to the activity. The owning partition's
        // RLS must reject it — the live node must NOT reflect the intruder's
        // attempted mutation.
        accessService.SetContext(new AccessContext { ObjectId = NonOwner, Name = NonOwner });
        try
        {
            await workspace.GetMeshNodeStream(activityPath).Update(node =>
            {
                if (node.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions, null) is not { } log)
                    return node;
                return node with
                {
                    Content = log with
                    {
                        Messages = log.Messages.Add(new LogMessage("INTRUDER", Microsoft.Extensions.Logging.LogLevel.Warning))
                    }
                };
            }).FirstAsync().Timeout(10.Seconds()).ToTask();
        }
        catch
        {
            // A denied write may surface as OnError — acceptable; the assertion
            // below is the authoritative check on the durable state.
        }

        accessService.SetContext(null);

        // Authoritative check on durable state (the content read seam is
        // System-backed internally, so this observes the true persisted node):
        // the intruder line never landed.
        var node = await workspace.GetMeshNodeStream(activityPath)
            .Where(n => n is not null).FirstAsync().Timeout(10.Seconds()).ToTask();
        var log = node!.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions, null);
        Assert.NotNull(log);
        Assert.DoesNotContain(log!.Messages, m => m.Message == "INTRUDER");
    }

    private static async Task<string> WaitFor(Func<string?> get) =>
        await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .Select(_ => get())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask();

    // The MeshNode content read seam opens under System internally (the cache's
    // hydration identity), so the read identity is not the gate here — we are
    // asserting on what the PRODUCER's writes durably left on the node.
    private async Task<ActivityLog> WaitForActivity(string activityPath, Func<ActivityLog, bool> predicate) =>
        await Mesh.GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(n => n?.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions, null))
            .Where(l => l is not null && predicate(l))
            .Select(l => l!)
            .FirstAsync()
            .Timeout(45.Seconds())
            .ToTask();
}
