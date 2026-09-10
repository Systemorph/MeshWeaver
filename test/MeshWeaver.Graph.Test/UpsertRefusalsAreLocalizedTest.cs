using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#3917 — the upsert handler's FAILURE surface was English-only while its SUCCESS surface was
/// localized.</b> <c>PostOk</c> wrote <c>new LogMessage(logLine, Information).WithKey(logKey, …)</c>;
/// <c>PostFail</c> wrote <c>new LogMessage(error, Error)</c> with no key and — the reason it stayed
/// that way — <b>no key parameter to pass one through</b>. So every one of the handler's refusals
/// rendered in English for a German viewer, from "Node path and Id must not be empty" to the
/// NodeType and mis-scoped-grant refusals.
///
/// <para><b>Why keying only the newest refusals would have been worse than doing nothing.</b> The
/// same dialog would then show a German sentence for "the probe produced no answer" and an English
/// one for "the probe faulted" — a viewer cannot tell that apart from a translation bug. So the fix
/// is the whole surface at once, which is what this test measures.</para>
///
/// <para><b>The decision this pins, because it is the same question every <c>*Response.Error</c>
/// faces.</b> <c>CreateOrUpdateNodeResponse.Error</c> stays ENGLISH. It is a wire field: every
/// consumer of it in <c>src/</c> is a service — <c>ModuleDiscoveryService</c>,
/// <c>PackageInstaller</c>, <c>GitHubSyncService</c>, <c>GitHubWebhookProcessor</c>,
/// <c>IssueService</c>, <c>StaticRepoImporter</c> (which folds it into an exception message),
/// <c>NodeCopyHelper</c> — and not one of them is a viewer surface. Localizing it would translate a
/// value that code reads, matches and logs, for no viewer's benefit. The LOCALIZED surface is the
/// <see cref="ActivityLog"/> travelling on the same response, which is what a person actually
/// reads. Both halves are asserted below, so neither can be changed silently.</para>
///
/// <para><b>Both directions.</b> A refusal must render German for a German viewer AND English for
/// an English one — a fix that keyed everything to a single language, or that quietly dropped the
/// fallback, passes a one-sided test. And the SUCCESS path is asserted unchanged, so "everything
/// localizes now" cannot be reached by breaking what already worked.</para>
/// </summary>
public class UpsertRefusalsAreLocalizedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string MissingType = "No/Such/NodeType/For/3917";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static string NewId() => $"upsert-i18n-{Guid.NewGuid():N}";

    /// <summary>
    /// The refusal the handler composes itself, with no arguments — the simplest shape, and the one
    /// that proves the key/fallback pair reaches the transcript at all.
    /// </summary>
    [Fact]
    public async Task AHandlerOwnedRefusalRendersInTheViewersLanguage()
    {
        // An empty Id is refused before anything is read or written, so this drives exactly one
        // branch with no other machinery in the way.
        var response = await Upsert(new MeshNode("", TestPartition) { Name = "no-id" });

        response.Success.Should().BeFalse("an upsert with an empty Id must be refused");

        var refusal = LastMessage(response);
        refusal.MessageKey.Should().Be("activity.node.upsert.emptyPathOrId",
            "a refusal that the handler itself composes must carry a catalog key, or the German "
            + "viewer reads it in English forever — the transcript is a RENDERED surface and "
            + "nothing downstream can translate a stored sentence (#3236/#3917)");

        refusal.Localize("en").Should().Be("Node path and Id must not be empty");
        refusal.Localize("de").Should().Be("Node-Pfad und Id dürfen nicht leer sein");
        refusal.Localize("de").Should().NotBe(refusal.Localize("en"),
            "a German rendering identical to the English one means the key resolved to its "
            + "fallback — which is what an absent or misspelled de entry looks like");

        // 🚨 The wire field is the OTHER half of the decision, and it is deliberately English.
        response.Error.Should().Be(refusal.Message,
            "the stored fallback and the wire Error must be the SAME sentence — they are written "
            + "from one LocalizableText precisely so they cannot drift");
        response.Error.Should().Be(refusal.Localize("en"),
            "and that sentence is the English one: CreateOrUpdateNodeResponse.Error is consumed by "
            + "services, never rendered to a viewer, so it is not localized");
    }

    /// <summary>
    /// The refusal a SHARED guard composes several frames away, WITH arguments. This is the half
    /// that could not be fixed at the handler: it does not know which branch of
    /// <see cref="NodeTypeResolution"/> fired, so the key has to travel WITH the sentence.
    /// </summary>
    [Fact]
    public async Task ASharedGuardsRefusalCarriesItsKeyAndArgumentsAcrossFrames()
    {
        var id = NewId();
        await MeshService
            .CreateNode(new MeshNode(id, TestPartition) { Name = id, NodeType = "Markdown" })
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the node to retype must exist first");

        var response = await Upsert(new MeshNode(id, TestPartition) { Name = id, NodeType = MissingType });

        response.Success.Should().BeFalse();
        response.RejectionReason.Should().Be(NodeUpsertRejectionReason.InvalidNodeType);

        var refusal = LastMessage(response);
        refusal.MessageKey.Should().Be(NodeTypeResolution.RejectionMessageKey,
            "NodeTypeResolution composes this sentence, so it is the only frame that can name its "
            + "key — handing the handler a finished string throws the key away irrecoverably");

        refusal.MessageArgs.Should().NotBeNull();
        refusal.MessageArgs!["nodeType"].Should().Be(MissingType,
            "the arguments are what let a translator reorder the sentence for German word order; "
            + "a key with no arguments would render a sentence naming no type");
        refusal.MessageArgs["path"].Should().Be($"{TestPartition}/{id}");

        refusal.Localize("de").Should().Contain(MissingType,
            "the German rendering must still name the type — a translation that drops the "
            + "placeholder deletes the only information the line carries");
        refusal.Localize("de").Should().NotBe(refusal.Localize("en"));
        refusal.Localize("en").Should().Be(
            NodeTypeResolution.RejectionMessage($"{TestPartition}/{id}", MissingType),
            "the English rendering must be byte-identical to the builder's own output, or the "
            + "catalog and the fallback have drifted and a viewer sees two different sentences "
            + "depending on which one resolves");
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL. A fix that keyed the failure surface by breaking the success surface
    /// — or one that simply keyed every entry to one language — passes both tests above. This holds
    /// the half that already worked.
    /// </summary>
    [Fact]
    public async Task ASuccessfulUpsertStillKeysItsConfirmation()
    {
        var id = NewId();
        var response = await Upsert(new MeshNode(id, TestPartition) { Name = id, NodeType = "Markdown" });

        response.Success.Should().BeTrue($"the upsert must land; Error was: {response.Error}");

        var confirmation = LastMessage(response);
        confirmation.MessageKey.Should().Be("activity.node.created");
        confirmation.Localize("de").Should().NotBe(confirmation.Localize("en"),
            "the success surface was already localized before #3917 and must stay that way — this "
            + "is the control that stops the failure fix from being bought with the success half");
    }

    private static LogMessage LastMessage(CreateOrUpdateNodeResponse response)
    {
        response.Log.Should().NotBeNull(
            "the ActivityLog IS the localized surface — a response without one has nothing a "
            + "viewer could read in their own language");
        return response.Log!.Messages.Last();
    }

    private async Task<CreateOrUpdateNodeResponse> Upsert(MeshNode node)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        // ObserveNodeOperation, never Mesh.Observe: the test base's Mesh IS the router, and a
        // node-CRUD request issued from it would run the write on the router's own action block.
        var response = await access
            .RunAsSystem(() => ObserveNodeOperation(new CreateOrUpdateNodeRequest(node)))
            .FirstAsync()
            .Select(d => d.Message)
            .Timeout(TestTimeouts.Convergence)
            .Await();
        Output.WriteLine($"upsert success={response.Success} reason={response.RejectionReason} "
            + $"error={response.Error}");
        return response;
    }
}
