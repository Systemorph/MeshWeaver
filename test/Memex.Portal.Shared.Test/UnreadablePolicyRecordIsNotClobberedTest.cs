#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 A FAILED READ MUST NOT BECOME A WRITE — #3542, proposal 3, the half #3607 did not close.
///
/// <para>#3607 made an ABSENT <c>policy</c> field read as <see cref="UpdatePolicyKind.None"/> rather
/// than as "enabled". That fixed what an absent field MEANS; it did not stop the record from losing
/// the field. What loses it is a bookkeeping write built on
/// <c>UpdatePolicyNodeType.ParseContent</c>, which answers <c>new UpdatePolicyContent()</c> for BOTH
/// "there is no content" and "the content is present and this build cannot read it" — and the write
/// then PERSISTS that empty record over the admin's policy, the latest available tag, every combo
/// verdict and any live availability hold.</para>
///
/// <para><b>Measured before the fix, on <c>origin/main</c> at 5453be493.</b> Seeded
/// <c>{"policy":"None","latestAvailableTag":"3.0.0-ci.8009","comboVerifications":"corrupt"}</c> —
/// one field this build cannot deserialize, which is why the record reaches the write lambda as an
/// untyped <c>JsonElement</c> ("stayed an untyped JsonElement", logged by the stream cache). One
/// <c>RecordAvailable</c>-shaped write then completed SILENTLY and left
/// <c>{"requireCiGreen":true,"latestAvailableTag":"3.0.0-ci.9999","comboVerifications":[]}</c>: no
/// <c>policy</c> at all — the production state #3542 opens on, from a single bookkeeping write.</para>
///
/// <para><b>The fix is the framework's own typed write</b>, not a bespoke guard:
/// <c>Update&lt;UpdatePolicyContent&gt;((node, cur) =&gt; …)</c> hands the lambda <c>null</c> only
/// when the content is ABSENT and faults with a <c>MeshNodeStreamException</c> when it is present
/// and unreadable — the write does not happen. Every bookkeeping caller already wraps its write in a
/// <c>.Catch</c> that logs and carries on (#1020: a bookkeeping write may never gate the roll), so
/// an unreadable record is refused loudly and left intact.</para>
///
/// <para>🚨 Moving <c>lastCheckedAt</c> to a separate node — the other repair #3542 proposes — would
/// NOT have closed this. The three sibling writes (<c>RecordAvailable</c>, <c>RecordHold</c>,
/// <c>RecordVerification</c>) have the same shape on the same record, so the defect is
/// read-failure-becomes-a-default, not the co-location. Hence all four are pinned here.</para>
///
/// <para>🚨 Every arm has a positive control on a READABLE record. Without them, "the write was
/// refused" would be satisfied by a write that never works at all.</para>
/// </summary>
public class UnreadablePolicyRecordIsNotClobberedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A real record carrying an explicit policy and a real tag, with ONE field this build
    /// cannot deserialize (<c>comboVerifications</c> is an
    /// <c>ImmutableList&lt;ComboVerification&gt;</c>) — a field written by a build whose content
    /// type differed. The record as a whole is then unreadable.</summary>
    private const string UnreadableRecord =
        """{"policy":"None","requireCiGreen":true,"latestAvailableTag":"3.0.0-ci.8009","comboVerifications":"written-by-a-shape-this-build-cannot-read"}""";

    private const string ReadableRecord =
        """{"$type":"UpdatePolicyContent","policy":"None","requireCiGreen":true,"latestAvailableTag":"3.0.0-ci.8009"}""";

    private const string Candidate = "3.0.0-ci.9999";

    /// <summary>🚨 <see cref="TestTimeouts.Convergence"/>, never a literal: a hand-written 30 s is
    /// both a guess about machine speed AND the framework's own write bound (#2819).</summary>
    private static TimeSpan Budget => TestTimeouts.Convergence;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddUpdatePolicyType();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    public static IEnumerable<object[]> EveryBookkeepingWrite() =>
    [
        ["RecordAvailable"], ["RecordCheck"], ["RecordHold"], ["RecordVerification"],
    ];

    /// <summary>
    /// THE REGRESSION. Each of the four writes on <c>Admin/UpdatePolicy</c>, against a record this
    /// build cannot read: the write must be REFUSED, and the stored bytes must still carry the
    /// admin's <c>policy</c> and the tag that was there.
    /// </summary>
    [Theory(Timeout = 180000)]
    [MemberData(nameof(EveryBookkeepingWrite))]
    public async Task AnUnreadableRecordIsRefused_NotOverwritten(string write)
    {
        await Seed(UnreadableRecord);

        var fault = await RunWrite(write);

        Output.WriteLine($"{write} -> {fault?.GetType().Name ?? "COMPLETED"}: {fault?.Message}");
        fault.Should().NotBeNull(
            $"{write} read a record it could not parse, so it must refuse rather than write a "
            + "default-valued record over it");

        // 🚨 A single read is sound HERE and only here: the refusal happens INSIDE the update
        // lambda, which has therefore already run against this hub's mirror, and no patch was
        // posted — so there is no later emission to wait for. The positive control below, where a
        // real write does have to propagate, waits on the condition instead.
        var after = await ReadRawJson();
        Output.WriteLine($"after {write}: {after}");
        after.Should().Contain("\"policy\"",
            "the admin's explicit choice must survive a bookkeeping write that could not read it — "
            + "losing this field is how memex-cloud rolled itself onto a withdrawn line (#3542)");
        after.Should().Contain("3.0.0-ci.8009",
            "nothing on the record may be replaced by a default the write invented");
    }

    /// <summary>
    /// THE POSITIVE CONTROL, one per write. On a READABLE record the very same call must land AND
    /// leave the policy alone — otherwise the assertion above would pass for a write that is simply
    /// broken.
    /// </summary>
    [Theory(Timeout = 180000)]
    [MemberData(nameof(EveryBookkeepingWrite))]
    public async Task AReadableRecordIsWritten_AndKeepsItsPolicy(string write)
    {
        await Seed(ReadableRecord);

        var fault = await RunWrite(write);

        Output.WriteLine($"{write} -> {fault?.GetType().Name ?? "COMPLETED"}: {fault?.Message}");
        fault.Should().BeNull($"{write} must still land on a record it CAN read");

        // 🚨 Waits for the write's OWN field on the live stream — never a one-shot read, which is
        // free to answer with the pre-write snapshot and assert something it never observed
        // (measured while writing this: all three poller arms "failed" that way while the write had
        // in fact landed).
        var content = await WaitForContent(c => Landed(write, c));

        content.DeclaredPolicy.Should().Be(UpdatePolicyKind.None,
            "a bookkeeping write touches only its own fields");
    }

    private static bool Landed(string write, UpdatePolicyContent c) => write switch
    {
        "RecordAvailable" => c.LatestAvailableTag == Candidate,
        "RecordCheck" => c.LastCheckedAt is not null,
        "RecordHold" => c.HeldTag == Candidate,
        "RecordVerification" => c.ComboVerifications.Count == 1,
        _ => throw new ArgumentOutOfRangeException(nameof(write), write, "unknown write"),
    };

    /// <summary>Runs one of the four PRODUCTION writes and returns the fault it produced, or null
    /// when it completed. Nothing here re-implements a write shape: <c>RecordVerification</c> is the
    /// public API, and the other three are the poller's own <c>protected virtual</c> members reached
    /// through a derived class.</summary>
    private Task<Exception?> RunWrite(string write)
    {
        var poller = new ExposedPoller(Mesh);
        var run = write switch
        {
            "RecordAvailable" => poller.Available(Candidate),
            "RecordCheck" => poller.Check(),
            "RecordHold" => poller.Hold(Candidate),
            "RecordVerification" => UpdatePolicyNodeType.RecordVerification(
                Mesh,
                new ComboVerification
                {
                    CandidateTag = Candidate,
                    Verdict = ComboVerdictKind.Green,
                    VerifiedAt = DateTimeOffset.UtcNow,
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(write), write, "unknown write"),
        };
        return run
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);
    }

    /// <summary>The poller with its three bookkeeping writes exposed. Nothing else is overridden —
    /// the writes under test are the production ones, byte for byte.</summary>
    private sealed class ExposedPoller(IMessageHub hub)
        : SelfUpdateHostedService(hub, new NoTags(), new NoPatch(), new SelfUpdateOptions())
    {
        public IObservable<Unit> Available(string tag) => RecordAvailable(tag);

        public IObservable<Unit> Check() =>
            RecordCheck(SelfUpdateTrigger.SafetyNet, SelfUpdateVerdict.DetectOnly(Candidate));

        public IObservable<Unit> Hold(string tag) =>
            RecordHold(tag, UpdatabilityVerdict.Unavailable("a package cannot survive this release"));
    }

    private sealed class NoTags : IAcrTagLister
    {
        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoPatch : IDeploymentUpdater
    {
        public bool CanPatch => false;

        public Task<DateTimeOffset?> LastRolledAtAsync(CancellationToken ct) =>
            Task.FromResult<DateTimeOffset?>(null);

        public Task PatchToVersionAsync(string versionTag, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>The stored bytes, not a parse of them: "the record still carries its policy" is a
    /// fact about what is ON the node, and reading it through the parser that fails closed to
    /// <c>None</c> would answer <c>None</c> for a record that has no policy at all.</summary>
    private Task<string> ReadRawJson() =>
        RawContent()
            .Select(c => c switch
            {
                null => "<null>",
                JsonElement je => je.GetRawText(),
                _ => JsonSerializer.Serialize(c, Mesh.JsonSerializerOptions),
            })
            .Catch((Exception ex) => Observable.Return($"<read faulted: {ex.Message}>"))
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

    private Task<UpdatePolicyContent> WaitForContent(Func<UpdatePolicyContent, bool> settled) =>
        RawContent()
            .Select(c => UpdatePolicyNodeType.ParseContent(c, Mesh.JsonSerializerOptions))
            .Where(settled)
            .FirstAsync()
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

    private IObservable<object?> RawContent() =>
        Observable.Create<object?>(observer =>
        {
            using (Access.ImpersonateAsSystem())
                return Mesh.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
                    .Where(n => n is not null)
                    .Select(n => (object?)n.Content)
                    .Subscribe(observer);
        });

    private Task Seed(string json)
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode(UpdatePolicyNodeType.NodeId, UpdatePolicyNodeType.AdminPartition)
        {
            NodeType = UpdatePolicyNodeType.NodeType,
            Name = "Update Policy",
            State = MeshNodeState.Active,
            Content = JsonDocument.Parse(json).RootElement.Clone(),
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
}
