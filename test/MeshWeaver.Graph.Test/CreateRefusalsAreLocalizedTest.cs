using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#4507 — the CREATE and BULK-CREATE legs' failure surface was English-only, all of it.</b>
/// <c>CreateNodeResponse.Error</c> and <c>CreateNodesResponse.Error</c> were plain strings and every
/// sentence written into them was an English literal, from <c>"NodeType '{type}' is not
/// registered"</c> to <c>"Duplicate path in batch"</c> to the post-creation rollback report. The
/// handler even said so in a comment — <i>"English only, and NOT an oversight:
/// CreateNodeResponse.Fail carries no ActivityLog, so this path has no keyed surface to render
/// into"</i> — naming the transcript as the follow-up #3917 owed. This is that follow-up, and the
/// reason it converts the WHOLE surface at once is <see cref="UpsertRefusalsAreLocalizedTest"/>'s:
/// a viewer cannot tell "this branch was not keyed yet" from "the translation is broken".
///
/// <para><b>The decision pinned here, both halves.</b> <c>Error</c> stays ENGLISH — it is a wire
/// field every consumer in <c>src/</c> folds into a log line or an exception message
/// (<c>PackageInstaller</c>, <c>ModuleDiscoveryService</c>, <c>GitHubSyncService</c>,
/// <c>StaticRepoImporter</c>, <c>NodeCopyHelper</c>), exactly as the upsert leg records for
/// <c>CreateOrUpdateNodeResponse.Error</c>. The LOCALIZED surface is the <see cref="ActivityLog"/>
/// on the same response. Both are asserted, so neither can move silently.</para>
///
/// <para>🚨 <b>And the create leg has one thing the upsert leg does not: an EXCEPTION boundary.</b>
/// The sanctioned surface (<c>IMeshService.CreateNode</c>) reports failure by throwing, so a
/// response-only fix would leave the one screen a person actually reads — the Create form's error
/// dialog — in English forever. <see cref="TheKeyedRefusalSurvivesTheExceptionBoundary"/> is the
/// test that would have caught that, and it is the acceptance criterion of the issue.</para>
/// </summary>
public class CreateRefusalsAreLocalizedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string MissingType = "No/Such/NodeType/For/4507";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static string NewId() => $"create-i18n-{Guid.NewGuid():N}";

    /// <summary>
    /// The refusal the handler composes itself, with no arguments — the simplest shape, and the one
    /// that proves the key reaches the transcript at all. An empty Id is refused before anything is
    /// read or written, so exactly one branch runs with no other machinery in the way.
    /// </summary>
    [Fact]
    public async Task AHandlerOwnedRefusalRendersInTheViewersLanguage()
    {
        var response = await Create(new MeshNode("", TestPartition) { Name = "no-id" });

        response.Success.Should().BeFalse("a create with an empty Id must be refused");

        var refusal = LastMessage(response);
        refusal.MessageKey.Should().Be(CreateNodesRequest.EmptyPathOrIdKey,
            "a refusal the handler itself composes must carry a catalog key, or the German viewer "
            + "reads it in English forever — nothing downstream can translate a finished sentence");

        refusal.Localize("en").Should().Be("Node path and Id must not be empty");
        refusal.Localize("de").Should().Be("Node-Pfad und Id dürfen nicht leer sein");
        refusal.Localize("de").Should().NotBe(refusal.Localize("en"),
            "a German rendering identical to the English one means the key resolved to its "
            + "fallback — which is exactly what an absent or misspelled de entry looks like");

        // 🚨 The wire field is the OTHER half of the decision, and it is deliberately English.
        response.Error.Should().Be(refusal.Message,
            "the stored fallback and the wire Error are written from ONE LocalizableText precisely "
            + "so they cannot drift");
        response.Error.Should().Be(refusal.Localize("en"),
            "and that sentence is the English one: CreateNodeResponse.Error is consumed by services "
            + "and folded into exception messages, never rendered to a viewer");
    }

    /// <summary>
    /// A refusal WITH arguments, from the branch the issue names first. The arguments are what let a
    /// translator move the type name for German word order; a key with none would render a sentence
    /// naming no type at all.
    /// </summary>
    [Fact]
    public async Task ARefusalWithArgumentsKeepsThemForTheTranslator()
    {
        var id = NewId();
        var response = await Create(
            new MeshNode(id, TestPartition) { Name = id, NodeType = MissingType });

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeCreationRejectionReason.InvalidNodeType);

        var refusal = LastMessage(response);
        refusal.MessageKey.Should().Be("activity.node.create.nodeTypeNotRegistered");
        refusal.MessageArgs.Should().NotBeNull();
        refusal.MessageArgs!["nodeType"].Should().Be(MissingType);

        refusal.Localize("en").Should().Be($"NodeType '{MissingType}' is not registered",
            "the English rendering must be byte-identical to the sentence the handler composed, or "
            + "the catalog and the fallback have drifted and a viewer sees two different sentences "
            + "depending on which one resolves");
        refusal.Localize("de").Should().Contain(MissingType,
            "the German rendering must still name the type — a translation that drops the "
            + "placeholder deletes the only information the line carries");
        refusal.Localize("de").Should().NotBe(refusal.Localize("en"));
    }

    /// <summary>
    /// 🚨 THE ACCEPTANCE CRITERION. <c>IMeshService.CreateNode</c> reports failure as an EXCEPTION,
    /// and <c>ex.Message</c> is <c>CreateNodeResponse.Error</c> — English by contract. The Create
    /// form's error dialog sees nothing else, which is why localizing only the response would have
    /// changed nothing at all for the one person this issue is about.
    ///
    /// <para>On the old code this reads <c>null</c>: nothing was stamped, because nothing was keyed.</para>
    /// </summary>
    [Fact]
    public async Task TheKeyedRefusalSurvivesTheExceptionBoundary()
    {
        var id = NewId();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var outcome = await access
            .RunAsSystem(() => MeshService.CreateNode(
                new MeshNode(id, TestPartition) { Name = id, NodeType = MissingType }))
            .Materialize()
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);

        outcome.Kind.Should().Be(NotificationKind.OnError,
            "a create of an unregistered NodeType must fault the observable — without that the "
            + "assertions below would be measuring a success");

        var ex = outcome.Exception!;
        ex.Message.Should().Be($"NodeType '{MissingType}' is not registered",
            "the exception message is the wire Error, and it stays English so a service folding it "
            + "into a log line keeps grepping in one language");

        var refusal = ex.RefusalText();
        refusal.Should().NotBeNull(
            "the create leg's ONE viewer surface sees only the exception, so the keyed refusal has "
            + "to cross this boundary — NodeCreationFailure stamps it on Exception.Data exactly as "
            + "it already stamps the typed rejection reason and the MeshNodeError");
        refusal!.Localize("de").Should().NotBe(refusal!.Localize("en"),
            "and it must still resolve in the viewer's language on the far side");
        refusal!.Localize("en").Should().Be(ex.Message,
            "the two renderings of one sentence must agree for an English viewer");
    }

    /// <summary>
    /// The BULK leg, on a refusal whose key is composed by <see cref="CreateNodesRequest"/> itself
    /// — several frames from the handler that reports it. That distance is the whole reason the key
    /// travels WITH the sentence: the handler cannot know which of the four structural branches
    /// fired, so it could name neither the key nor its arguments.
    /// </summary>
    [Fact]
    public async Task TheBulkLegCarriesAGuardsKeyAcrossFrames()
    {
        var id = NewId();
        var satellite = new MeshNode(id, $"{TestPartition}/_Thread")
        {
            Name = id,
            NodeType = "Markdown",
        };

        var response = await CreateMany([satellite]);

        response.Success.Should().BeFalse("a satellite path may not travel in a bulk create");
        response.FailedPath.Should().Be(satellite.Path);

        var refusal = LastMessage(response);
        refusal.MessageKey.Should().Be(CreateNodesRequest.SatellitePathKey);
        refusal.MessageArgs!["path"].Should().Be(satellite.Path);
        refusal.Localize("de").Should().NotBe(refusal.Localize("en"));
        response.Error.Should().Be(refusal.Localize("en"),
            "the bulk response's Error is the same English wire value as the singular leg's");
    }

    /// <summary>
    /// And a refusal the bulk handler composes itself, so the leg is covered on both sides of the
    /// guard boundary. A duplicate path is a property of the BATCH, which is why it lives in the
    /// handler rather than on the request type.
    /// </summary>
    [Fact]
    public async Task TheBulkHandlersOwnRefusalIsKeyedToo()
    {
        var id = NewId();
        var node = new MeshNode(id, TestPartition) { Name = id, NodeType = "Markdown" };

        var response = await CreateMany([node, node]);

        response.Success.Should().BeFalse("the same path twice in one batch must be refused");

        var refusal = LastMessage(response);
        refusal.MessageKey.Should().Be("activity.node.bulkCreate.duplicatePath");
        refusal.Localize("en").Should().Be($"Duplicate path in batch: '{node.Path}'");
        refusal.Localize("de").Should().Contain(node.Path);
        refusal.Localize("de").Should().NotBe(refusal.Localize("en"));
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL. Every assertion above is about a REFUSAL, and a mesh that refused
    /// every create would satisfy all of them. This is the same create of a type that DOES resolve,
    /// by the same identity into the same partition — so a green suite cannot mean "creates are
    /// broken here".
    /// </summary>
    [Fact]
    public async Task ACreateThatShouldLandStillLands()
    {
        var id = NewId();
        var response = await Create(
            new MeshNode(id, TestPartition) { Name = id, NodeType = "Markdown" });

        response.Success.Should().BeTrue($"the create must land; Error was: {response.Error}");
        response.Error.Should().BeNull();
        response.Log.Should().BeNull(
            "the transcript exists to carry a REFUSAL — a successful create attaches none, and a "
            + "reader of Log must not have to ask whether it means failure");
    }

    private static LogMessage LastMessage(CreateNodeResponse response)
    {
        response.Log.Should().NotBeNull(
            "the ActivityLog IS the localized surface — a failed response without one has nothing "
            + "a viewer could read in their own language");
        return response.Log!.Messages.Last();
    }

    private static LogMessage LastMessage(CreateNodesResponse response)
    {
        response.Log.Should().NotBeNull(
            "the bulk leg needs the same localized surface as the singular one, or half the create "
            + "surface stays English and a viewer cannot tell why");
        return response.Log!.Messages.Last();
    }

    private async Task<CreateNodeResponse> Create(MeshNode node)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        // ObserveNodeOperation, never Mesh.Observe: the test base's Mesh IS the router, and a
        // node-CRUD request issued from it would run the write on the router's own action block.
        var response = await access
            .RunAsSystem(() => ObserveNodeOperation(new CreateNodeRequest(node)))
            .FirstAsync()
            .Select(d => d.Message)
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"create success={response.Success} reason={response.RejectionReason} "
            + $"error={response.Error}");
        return response;
    }

    private async Task<CreateNodesResponse> CreateMany(ImmutableList<MeshNode> nodes)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var response = await access
            .RunAsSystem(() => ObserveNodeOperation(new CreateNodesRequest(nodes)))
            .FirstAsync()
            .Select(d => d.Message)
            .Timeout(TestTimeouts.Convergence)
            .Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"bulk create success={response.Success} reason={response.RejectionReason} "
            + $"failedPath={response.FailedPath} error={response.Error}");
        return response;
    }
}
