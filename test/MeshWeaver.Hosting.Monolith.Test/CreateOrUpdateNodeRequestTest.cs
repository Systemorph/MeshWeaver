using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Focused tests for the <see cref="CreateOrUpdateNodeRequest"/> mesh-hub
/// handler. Two strict paths cover the contract: missing target → forwards
/// as <see cref="CreateNodeRequest"/>, existing target → applies via
/// <c>workspace.GetMeshNodeStream(path).Update(state =&gt;
/// UpdateAccordingToSourceNode(state, sourceNode))</c>. Direct
/// <c>persistence.Write</c> is explicitly disallowed by the
/// "per-node hub is the sole owner of its state" rule that this handler
/// enforces.
/// </summary>
public class CreateOrUpdateNodeRequestTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Missing target case: the upsert falls through to <see cref="CreateNodeRequest"/>
    /// internally; the response carries <see cref="CreateOrUpdateNodeResponse.WasCreated"/> = true.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Upsert_OnMissingTarget_CreatesAndReports_WasCreated_True()
    {
        var path = $"{TestPartition}/upsert-create-{Guid.NewGuid():N}";
        var sourceNode = new MeshNode(path.Split('/').Last(), TestPartition)
        {
            Name = "Brand-new node",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# fresh content" },
            State = MeshNodeState.Active,
        };

        var resp = await ObserveNodeOperation(new CreateOrUpdateNodeRequest(sourceNode))
            .Select(d => d.Message)
            .Should().Emit();

        resp.Success.Should().BeTrue(resp.Error ?? "");
        resp.WasCreated.Should().BeTrue(
            "the target was missing — the handler must forward as CreateNodeRequest internally");
        resp.Node.Should().NotBeNull();
        resp.Node!.Path.Should().Be(path);
        resp.Node.Name.Should().Be("Brand-new node");
        resp.Log.Should().NotBeNull("every upsert rides on a single ActivityLog");

        // Verify the node lives in the mesh — single-node read via per-node hub.
        var live = await Mesh.GetMeshNode(path, 10.Seconds()).Should().Emit();
        live.Should().NotBeNull();
        live!.Name.Should().Be("Brand-new node");
        live.Content.Should().BeOfType<MarkdownContent>()
            .Which.Content.Should().Be("# fresh content");
    }

    /// <summary>
    /// Existing target case: the upsert applies through
    /// <c>workspace.GetMeshNodeStream(path).Update(...)</c>; the response
    /// carries <see cref="CreateOrUpdateNodeResponse.WasCreated"/> = false and
    /// the post-update node has the source's writable fields merged in.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Upsert_OnExistingTarget_UpdatesViaStream_WasCreated_False()
    {
        var path = $"{TestPartition}/upsert-update-{Guid.NewGuid():N}";

        // Seed an existing node — first via NodeFactory so the per-node hub
        // is alive and owns the state we'll update through GetMeshNodeStream.
        await NodeFactory.CreateNode(new MeshNode(path.Split('/').Last(), TestPartition)
        {
            Name = "Original",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# v1" },
            State = MeshNodeState.Active,
        }).Should().Emit();

        // Send the upsert with the same path but new writable fields. The
        // handler must take the existence path and apply via the stream.
        var sourceNode = new MeshNode(path.Split('/').Last(), TestPartition)
        {
            Name = "Overwritten",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# v2" },
            State = MeshNodeState.Active,
        };

        var resp = await ObserveNodeOperation(new CreateOrUpdateNodeRequest(sourceNode))
            .Select(d => d.Message)
            .Should().Emit();

        resp.Success.Should().BeTrue(resp.Error ?? "");
        resp.WasCreated.Should().BeFalse(
            "the target existed — the handler must apply via stream.Update, not CreateNodeRequest");
        resp.Node.Should().NotBeNull();
        resp.Node!.Name.Should().Be("Overwritten");
        resp.Node.Content.Should().BeOfType<MarkdownContent>()
            .Which.Content.Should().Be("# v2");

        // Verify the live read agrees — wait for the stream to converge on
        // the new state. MeshNodeTypeSource debounces persistence saves over
        // 200ms; an immediate point-in-time read can race that. Subscribe to
        // the per-node hub's MeshNode stream and wait for the new Name.
        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var converged = await workspace.GetMeshNodeStream(path)
            .Should().Within(10.Seconds())
            .Match(n => n?.Name == "Overwritten");
        converged.Name.Should().Be("Overwritten");
        converged.Content.Should().BeOfType<MarkdownContent>()
            .Which.Content.Should().Be("# v2");
    }

    /// <summary>
    /// Existence preservation: identity fields (Id, Path, CreatedDate,
    /// CreatedBy) on the existing node MUST NOT be overwritten by the
    /// source's defaults — only the writable surface (Name, NodeType, Icon,
    /// Category, Content, State, PreRenderedHtml) flows through.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Upsert_OnExistingTarget_PreservesIdentityFields()
    {
        var path = $"{TestPartition}/upsert-identity-{Guid.NewGuid():N}";

        await NodeFactory.CreateNode(new MeshNode(path.Split('/').Last(), TestPartition)
        {
            Name = "Original",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# original" },
            State = MeshNodeState.Active,
        }).Should().Emit();
        var before = await Mesh.GetMeshNode(path, 10.Seconds()).Should().Emit();
        before.Should().NotBeNull();
        var originalCreatedDate = before!.CreatedDate;
        var originalCreatedBy = before.CreatedBy;

        var resp = await ObserveNodeOperation(new CreateOrUpdateNodeRequest(
                new MeshNode(path.Split('/').Last(), TestPartition)
                {
                    Name = "Renamed",
                    NodeType = "Markdown",
                    Content = new MarkdownContent { Content = "# updated" },
                    State = MeshNodeState.Active,
                }))
            .Select(d => d.Message)
            .Should().Emit();
        resp.Success.Should().BeTrue(resp.Error ?? "");

        // Wait for the stream to converge on the renamed state.
        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var after = await workspace.GetMeshNodeStream(path)
            .Should().Within(10.Seconds())
            .Match(n => n?.Name == "Renamed");
        after.CreatedDate.Should().Be(originalCreatedDate,
            "CreatedDate is identity — UpdateAccordingToSourceNode preserves it");
        after.CreatedBy.Should().Be(originalCreatedBy,
            "CreatedBy is identity — UpdateAccordingToSourceNode preserves it");
        after.Path.Should().Be(path, "Path is identity");
        after.Name.Should().Be("Renamed", "Name is writable — should overwrite");
    }

    /// <summary>
    /// The no-op guard: an upsert IDENTICAL to the persisted state must be acknowledged without
    /// reaching the owner — no Version mint, no LastModified re-stamp, no history row, no stream
    /// re-broadcast (the deploy-flicker source when a full re-sync rewrites unchanged nodes).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Upsert_WithIdenticalState_IsSkipped_NoVersionOrTimestampChurn()
    {
        var path = $"{TestPartition}/upsert-noop-{Guid.NewGuid():N}";
        MeshNode Node() => new(path.Split('/').Last(), TestPartition)
        {
            Name = "Same",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# identical" },
            State = MeshNodeState.Active,
        };

        await NodeFactory.CreateNode(Node()).Should().Emit();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var before = await ReadStable(storage, path);

        var resp = await ObserveNodeOperation(new CreateOrUpdateNodeRequest(Node()))
            .Select(d => d.Message)
            .Should().Emit();

        resp.Success.Should().BeTrue(resp.Error ?? "");
        resp.WasCreated.Should().BeFalse();
        resp.Node.Should().NotBeNull();
        resp.Log!.Messages.Any(m => m.Message.Contains("no-op", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("the skip must be visible in the activity log, not a silent success");

        // The persisted stamps prove no write reached the owner: any write re-stamps LastModified
        // and mints a fresh Version unconditionally. ReadStable's quiet window (>1.2s, well past
        // the 200ms persist debounce) would catch a stray write.
        var after = await ReadStable(storage, path);
        after.Version.Should().Be(before.Version, "an identical upsert must not mint a Version");
        after.LastModified.Should().Be(before.LastModified, "an identical upsert must not re-stamp LastModified");
    }

    /// <summary>
    /// The guard must not over-skip: a single changed writable field (here Name; content identical)
    /// still takes the write path.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Upsert_WithOneChangedField_StillWrites()
    {
        var path = $"{TestPartition}/upsert-nearnoop-{Guid.NewGuid():N}";
        await NodeFactory.CreateNode(new MeshNode(path.Split('/').Last(), TestPartition)
        {
            Name = "Before",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# same content" },
            State = MeshNodeState.Active,
        }).Should().Emit();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var before = await ReadStable(storage, path);

        var resp = await ObserveNodeOperation(new CreateOrUpdateNodeRequest(
                new MeshNode(path.Split('/').Last(), TestPartition)
                {
                    Name = "After",
                    NodeType = "Markdown",
                    Content = new MarkdownContent { Content = "# same content" },
                    State = MeshNodeState.Active,
                }))
            .Select(d => d.Message)
            .Should().Emit();
        resp.Success.Should().BeTrue(resp.Error ?? "");

        var after = await ReadStable(storage, path, n => n.Name == "After");
        after.Name.Should().Be("After");
        after.LastModified.Should().BeAfter(before.LastModified,
            "a real change takes the write path and re-stamps");
    }

    /// <summary>
    /// 🚨 Issue #748 — mesh-owned compile state survives EVERY upsert, whatever the writer's copy
    /// says. The incoming node carries a genuine AUTHORED change (Configuration) next to a STALE
    /// compile verdict: an older <c>LastCompiledVersion</c>, a dangling assembly pointer, and a
    /// failure status the live node long since moved past. That is the exact shape every upsert
    /// writer produces — a repo file embeds the verdict it was exported with, an installer ships the
    /// package author's, and a syncing client's snapshot comes from the eventually-consistent query
    /// index, which lags the compile pipeline because the compile does not run under the writer's
    /// lock. Letting it land is how a healthy type reverted to its previous state with a dangling
    /// release pointer on memex (2026-08-02), and how a weeks-old "Ok" parks a type on a cold cache.
    ///
    /// <para>The rule is enforced by the OWNER, inside the upsert's merge, so it holds for every
    /// writer rather than for the one that remembered to patch it up client-side. Deliberate compile
    /// writes are unaffected — they all go through <c>GetMeshNodeStream(path).Update</c>, never
    /// through an upsert.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Upsert_WithStaleCompileState_TakesAuthoredChange_AndKeepsLiveCompileState()
    {
        var id = $"upsert-nodetype-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";

        // The LIVE node: an authored definition plus the compile state the mesh currently owns.
        // CompiledFrameworkVersion stays null on purpose — a non-null MISMATCHING value would make
        // the framework-stale kickoff flip this type to Pending and race the assertion, and a
        // non-null status keeps the first-build kickoff out too. Empty source maps keep the sources
        // watcher idempotent from its first emission.
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "Widget",
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                Sources = [],
                CompilationStatus = CompilationStatus.Ok,
                LastCompiledVersion = 1082,
                LatestAssemblyCollection = "nodetype-cache",
                LatestAssemblyPath = "Widget/v1082.dll",
                CompiledSources = ImmutableDictionary<string, long>.Empty,
                CurrentSourceVersions = ImmutableDictionary<string, long>.Empty,
            },
        }).Should().Emit();

        // The upsert: authored change + a stale verdict the writer happened to be holding.
        var resp = await ObserveNodeOperation(new CreateOrUpdateNodeRequest(
                new MeshNode(id, TestPartition)
                {
                    Name = "Widget",
                    NodeType = MeshNode.NodeTypePath,
                    State = MeshNodeState.Active,
                    Content = new NodeTypeDefinition
                    {
                        Configuration = "config => CHANGED",
                        Sources = [],
                        CompilationStatus = CompilationStatus.Error,
                        CompilationError = "stale failure from the writer's copy",
                        LastCompiledVersion = 202,
                        LatestAssemblyCollection = "nodetype-cache",
                        LatestAssemblyPath = "Widget/v202.dll",
                    },
                }))
            .Select(d => d.Message)
            .Should().Emit();
        resp.Success.Should().BeTrue(resp.Error ?? "");

        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var converged = await workspace.GetMeshNodeStream(path)
            .Should().Within(30.Seconds())
            .Match(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)?.Configuration
                == "config => CHANGED");

        var def = converged.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions);
        def.Should().NotBeNull();
        def!.Configuration.Should().Be("config => CHANGED",
            "the repo/installer owns the AUTHORED definition — that change must land");
        // 1082L, not 1082 — LastCompiledVersion is long?, and the boxed compare would fail on the
        // int literal even when the value is right ("Expected 1082 … but found 1082").
        def.LastCompiledVersion.Should().Be(1082L,
            "the mesh owns the compile verdict — the writer's stale copy must not regress it");
        def.LatestAssemblyPath.Should().Be("Widget/v1082.dll",
            "a stale assembly pointer would send activation at bytes that no longer exist");
        def.CompilationStatus.Should().Be(CompilationStatus.Ok,
            "a healthy type must not be reverted to the writer's stale failure status");
        def.CompilationError.Should().BeNull(
            "an operational member ABSENT on the live node stays absent — the writer's value never seeds it");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  MainNode — issue #2631
    //
    //  MeshNode.MainNode is NOT nullable: it defaults to the node's own path, so "unset" and "set
    //  to self" are the same value on the wire. That is why the merge simply LEFT IT OUT — and why
    //  an upsert could never move one, while `GetMeshNodeStream(path).Update` could. The rule is
    //  MeshNode.HasExplicitMainNode: apply the source's MainNode only when it names something
    //  other than the node itself. The two directions are pinned separately below, because the
    //  naive fix (`source.MainNode ?? state.MainNode`) passes the first and DEMOTES EVERY
    //  SATELLITE on the second.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🚨 #2631 — the bug. An upsert whose ONLY difference from the stored node is MainNode was
    /// skipped as a no-op (<c>IsNoOpUpsert</c> never compared the field) and, had it taken the
    /// write path, dropped anyway (<c>UpdateAccordingToSourceNode</c> kept the stored value). The
    /// caller got <c>Success = true</c> and nothing moved: MeshWeaver.Plugins #839's
    /// <c>RefreshAppTiles</c> sweep reported "1390 of 1443 record(s) refreshed" on memex with not
    /// one <c>mainNode</c> changed.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Upsert_ChangingOnlyMainNode_IsApplied_NotSkippedAsANoOp()
    {
        var id = $"upsert-mainnode-move-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";
        var target = $"{TestPartition}/click-target-{Guid.NewGuid():N}";

        // A perfectly ordinary MAIN node — MainNode defaults to its own path.
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "Tile",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# tile" },
            State = MeshNodeState.Active,
        }).Should().Emit();

        // Everything identical EXCEPT MainNode: the whole point of the test.
        var resp = await ObserveNodeOperation(new CreateOrUpdateNodeRequest(
                new MeshNode(id, TestPartition)
                {
                    Name = "Tile",
                    NodeType = "Markdown",
                    Content = new MarkdownContent { Content = "# tile" },
                    State = MeshNodeState.Active,
                    MainNode = target,
                }))
            .Select(d => d.Message)
            .Should().Emit();

        resp.Success.Should().BeTrue(resp.Error ?? "");
        resp.Log!.Messages.Any(m => m.Message.Contains("no-op", StringComparison.OrdinalIgnoreCase))
            .Should().BeFalse(
                "MainNode is a real change — reporting it as a skipped no-op is exactly #2631");

        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var after = await ReadStable(storage, path, n => n.MainNode == target);
        after.MainNode.Should().Be(target,
            "an upsert that names a different MainNode must move it — the sweep in "
            + "MeshWeaver.Plugins #839 reported success and moved nothing");
    }

    /// <summary>
    /// 🚨 THE REGRESSION GUARD, and the reason the fix is not <c>source.MainNode ?? state.MainNode</c>.
    /// MainNode is non-nullable and defaults to the node's OWN path, so a writer that never touched
    /// it still sends a self-pointing value. Copying that blindly turns every SATELLITE the upsert
    /// touches into a main node (<c>is:main</c> is SQL <c>n.main_node = n.path</c>) — dropping it
    /// out of its owner's listings and re-scoping its grants, which project at
    /// <c>COALESCE(main_node, namespace)</c>. Here the upsert carries a genuine change (Name), so
    /// it takes the WRITE path: the merge itself must preserve the stored MainNode.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Upsert_WithDefaultSelfMainNode_PreservesAStoredSatelliteMainNode()
    {
        var id = $"upsert-satellite-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";
        var owner = $"{TestPartition}/owner-{Guid.NewGuid():N}";

        // A satellite: MainNode points at its primary, not at itself.
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "Before",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# satellite" },
            State = MeshNodeState.Active,
            MainNode = owner,
        }).Should().Emit();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        (await ReadStable(storage, path)).MainNode.Should().Be(owner, "seed precondition");

        // A writer that never thought about MainNode — so the source carries the SELF-PATH default.
        var source = new MeshNode(id, TestPartition)
        {
            Name = "After",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# satellite" },
            State = MeshNodeState.Active,
        };
        source.MainNode.Should().Be(path, "precondition: an untouched MainNode IS the node's path");
        source.HasExplicitMainNode.Should().BeFalse(
            "the self-path default must never read as a deliberate re-parenting");

        var resp = await ObserveNodeOperation(new CreateOrUpdateNodeRequest(source))
            .Select(d => d.Message)
            .Should().Emit();
        resp.Success.Should().BeTrue(resp.Error ?? "");

        var after = await ReadStable(storage, path, n => n.Name == "After");
        after.Name.Should().Be("After", "the real change must still land");
        after.MainNode.Should().Be(owner,
            "an untouched MainNode must NOT demote a satellite to a main node — a `?? state` "
            + "merge would silently do exactly that on every upsert");
    }

    /// <summary>
    /// The same guard on the COMPARE side: with nothing else changed either, the self-path default
    /// must not read as a change, so the whole upsert is still skipped — no Version mint, no
    /// LastModified re-stamp — and the satellite's MainNode is untouched.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Upsert_WithDefaultSelfMainNode_OnASatellite_IsStillANoOp()
    {
        var id = $"upsert-satellite-noop-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";
        var owner = $"{TestPartition}/owner-{Guid.NewGuid():N}";

        MeshNode Node(string? mainNode) => mainNode is null
            ? new MeshNode(id, TestPartition)
            {
                Name = "Same",
                NodeType = "Markdown",
                Content = new MarkdownContent { Content = "# same" },
                State = MeshNodeState.Active,
            }
            : new MeshNode(id, TestPartition)
            {
                Name = "Same",
                NodeType = "Markdown",
                Content = new MarkdownContent { Content = "# same" },
                State = MeshNodeState.Active,
                MainNode = mainNode,
            };

        await NodeFactory.CreateNode(Node(owner)).Should().Emit();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var before = await ReadStable(storage, path);
        before.MainNode.Should().Be(owner, "seed precondition");

        var resp = await ObserveNodeOperation(new CreateOrUpdateNodeRequest(Node(null)))
            .Select(d => d.Message)
            .Should().Emit();
        resp.Success.Should().BeTrue(resp.Error ?? "");
        resp.Log!.Messages.Any(m => m.Message.Contains("no-op", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue(
                "the self-path default is not a change — comparing it raw would make EVERY "
                + "re-import of every satellite take the write path");

        var after = await ReadStable(storage, path);
        after.Version.Should().Be(before.Version, "no change → no Version mint");
        after.LastModified.Should().Be(before.LastModified, "no change → no LastModified re-stamp");
        after.MainNode.Should().Be(owner, "and the satellite is still a satellite");
    }

    /// <summary>
    /// The no-op guard must keep holding once MainNode is part of the comparison: an upsert that
    /// restates the SAME explicit MainNode changes nothing and must not churn the node.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Upsert_RestatingTheSameExplicitMainNode_IsStillANoOp()
    {
        var id = $"upsert-mainnode-noop-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";
        var owner = $"{TestPartition}/owner-{Guid.NewGuid():N}";

        MeshNode Node() => new(id, TestPartition)
        {
            Name = "Same",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# identical" },
            State = MeshNodeState.Active,
            MainNode = owner,
        };

        await NodeFactory.CreateNode(Node()).Should().Emit();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var before = await ReadStable(storage, path);

        var resp = await ObserveNodeOperation(new CreateOrUpdateNodeRequest(Node()))
            .Select(d => d.Message)
            .Should().Emit();
        resp.Success.Should().BeTrue(resp.Error ?? "");
        resp.Log!.Messages.Any(m => m.Message.Contains("no-op", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("an identical upsert must still be skipped");

        var after = await ReadStable(storage, path);
        after.Version.Should().Be(before.Version, "an identical upsert must not mint a Version");
        after.LastModified.Should().Be(before.LastModified,
            "an identical upsert must not re-stamp LastModified");
        after.MainNode.Should().Be(owner);
    }

    /// <summary>
    /// A MAIN node stays a main node across an ordinary upsert: <c>MainNode == Path</c> is what
    /// <c>is:main</c> filters on, so a merge that got this wrong would drop the node out of its
    /// partition's own listings.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Upsert_OnAMainNode_KeepsItAMainNode()
    {
        var id = $"upsert-mainnode-stable-{Guid.NewGuid():N}";
        var path = $"{TestPartition}/{id}";

        await NodeFactory.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "Before",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# main" },
            State = MeshNodeState.Active,
        }).Should().Emit();

        var resp = await ObserveNodeOperation(new CreateOrUpdateNodeRequest(
                new MeshNode(id, TestPartition)
                {
                    Name = "After",
                    NodeType = "Markdown",
                    Content = new MarkdownContent { Content = "# main" },
                    State = MeshNodeState.Active,
                }))
            .Select(d => d.Message)
            .Should().Emit();
        resp.Success.Should().BeTrue(resp.Error ?? "");

        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var after = await ReadStable(storage, path, n => n.Name == "After");
        after.MainNode.Should().Be(path, "MainNode == Path is what makes this a MAIN node");
        after.MainNode.Should().Be(after.Path);
    }

    /// <summary>
    /// 🚨 #2939 — the UPDATE half, and the reason the create-path guard alone was not enough.
    /// A durable row carrying a stale self-default <c>MainNode</c> (a node minted in one partition
    /// and rebased into another with <c>with { Namespace = … }</c>) cannot be healed by the writer
    /// that re-imports it: a full-instance upsert can move a MainNode anywhere EXCEPT back onto the
    /// node's own path, because <c>MainNode == Path</c> is exactly what
    /// <see cref="MeshNode.HasExplicitMainNode"/> reads as "unset" (see its remarks). So the
    /// re-import compared equal, skipped as a no-op, and the row stayed outside <c>is:main</c>
    /// — SQL <c>n.main_node = n.path</c> — forever. SEVEN live nodes on memex.meshweaver.cloud were
    /// in this state, with <c>get</c> returning each of them perfectly.
    ///
    /// <para>🚨 Restoring the pointer is one of TWO required halves for those nodes to be findable
    /// again; the other is #2942 (a query union whose legacy single-<c>Query</c> field carries only
    /// <c>list[0]</c>). This test pins the half that lives here — the stored value — and claims
    /// nothing about search.</para>
    ///
    /// <para>The upsert now runs the same 1b′ repair on the MERGED node, so a re-import heals it.
    /// The no-op skip has to know that too, or the write it needs never happens.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Upsert_HealsAStoredStaleSelfDefaultMainNode_TheSourceCannotExpress()
    {
        var id = $"upsert-stale-mainnode-{Guid.NewGuid():N}";
        var ns = $"{TestPartition}/Skill";
        var path = $"{ns}/{id}";
        var stale = $"Skill/{id}";

        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

        // Planted straight into storage: the CREATE path repairs this shape now, so a durable row in
        // this state can only be a row written before the repair existed — which is exactly what the
        // measured nodes are.
        await storage.Write(
                new MeshNode(id, ns)
                {
                    Name = "Deployment",
                    NodeType = "Markdown",
                    Content = new MarkdownContent { Content = "# skill" },
                    State = MeshNodeState.Active,
                    MainNode = stale,
                    Version = 3,
                },
                Mesh.JsonSerializerOptions)
            .Should().Within(15.Seconds()).Emit();

        (await ReadStable(storage, path)).MainNode.Should().Be(stale, "seed precondition");

        // The re-import: byte-identical to the file it came from, and — crucially — its MainNode is
        // the node's own path, i.e. NOT explicit. Every field compares equal to the stored row.
        var source = new MeshNode(id, ns)
        {
            Name = "Deployment",
            NodeType = "Markdown",
            Content = new MarkdownContent { Content = "# skill" },
            State = MeshNodeState.Active,
        };
        source.HasExplicitMainNode.Should().BeFalse(
            "precondition: the source cannot even SAY 'point back at yourself'");

        var resp = await ObserveNodeOperation(new CreateOrUpdateNodeRequest(source))
            .Select(d => d.Message)
            .Should().Emit();
        resp.Success.Should().BeTrue(resp.Error ?? "");

        var after = await ReadStable(storage, path, n => n.MainNode == path);
        after.MainNode.Should().Be(path,
            "a re-import must heal a stale self-default MainNode — otherwise the only route back "
            + "is a hand-run GetMeshNodeStream(path).Update on a live portal");
    }

    // Reads until the persisted node satisfies the predicate AND its Version is unchanged across
    // 4 consecutive samples (~1.2s quiet — past the 200ms persist debounce), so enrichment/debounce
    // trails can't masquerade as churn.
    private async Task<MeshNode> ReadStable(
        IStorageAdapter storage, string path, Func<MeshNode, bool>? predicate = null)
    {
        MeshNode? last = null;
        var stable = 0;
        for (var i = 0; i < 100 && stable < 4; i++)
        {
            var current = await storage.Read(path, Mesh.JsonSerializerOptions).FirstAsync().Await();
            stable = current is not null && last is not null
                     && current.Version == last.Version
                     && (predicate is null || predicate(current))
                ? stable + 1
                : 0;
            last = current ?? last;
            await Task.Delay(300);
        }
        last.Should().NotBeNull($"node {path} must be persisted");
        return last!;
    }
}
