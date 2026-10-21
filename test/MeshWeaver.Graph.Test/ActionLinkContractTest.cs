#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.ActionLinks;
using MeshWeaver.Data;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the neutral action-link contract (MeshWeaver#4692): the WIRE form of an invocation, the
/// fail-closed handler resolution, the recipient/origin admission rules, the idempotency key and
/// the durable activity action reference — including that an activity written BEFORE the reference
/// existed still reads.
///
/// <para>The JSON literals below are the pinned version-1 wire forms. They are written out by hand
/// on purpose: a test that serialized and re-read with the same build would pass through any
/// rename, and a rename is exactly the break a stored link cannot survive.</para>
/// </summary>
public class ActionLinkContractTest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ActionSite Site = new() { SiteId = "portal", Origin = "https://portal.example.com" };

    private sealed class Handler(string key, int min = 1, int max = 1, params string[] capabilities) : IActionHandler
    {
        public int Order { get; init; }

        public ActionHandlerCapability Capability => new()
        {
            HandlerKey = key,
            MinVersion = min,
            MaxVersion = max,
            Order = Order,
            Capabilities = [.. capabilities],
        };

        public IObservable<ActionOutcome> Prepare(ActionInvocation invocation, ActionRecipientContext context) =>
            Observable.Return(ActionOutcome.Of(ActionOutcomeStatus.Ready, invocation));
    }

    private static ActionInvocation Invocation(string key = "store.coupon-journey", int version = 1,
        params string[] required) =>
        new() { HandlerKey = key, HandlerVersion = version, RequiredCapabilities = [.. required] };

    // ── Wire form ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheVersion1WireForm_OfAnInvocation_StillReads()
    {
        const string stored = """
            {"contractVersion":1,"handlerKey":"store.coupon-journey","handlerVersion":2,
             "payload":{"coupon":"SAMPLE","features":["renewal"]},
             "requiredCapabilities":["PaymentMethodSetup"]}
            """;

        var invocation = JsonSerializer.Deserialize<ActionInvocation>(stored, Json)!;

        invocation.ContractVersion.Should().Be(1);
        invocation.HandlerKey.Should().Be("store.coupon-journey");
        invocation.HandlerVersion.Should().Be(2);
        invocation.Payload!.Value.GetProperty("coupon").GetString().Should().Be("SAMPLE");
        invocation.RequiredCapabilities.Should().Equal(ActionCapabilities.PaymentMethodSetup);
    }

    [Fact]
    public void AnInvocation_RoundTrips_WithItsPayloadVerbatim()
    {
        var original = new ActionInvocation
        {
            HandlerKey = "store.coupon-journey",
            HandlerVersion = 3,
            Payload = JsonSerializer.SerializeToElement(new { coupon = "SAMPLE", days = 90 }, Json),
            RequiredCapabilities = [ActionCapabilities.PaymentMethodSetup, "module.own-capability"],
        };

        var json = JsonSerializer.Serialize(original, Json);
        var read = JsonSerializer.Deserialize<ActionInvocation>(json, Json)!;

        read.HandlerKey.Should().Be(original.HandlerKey);
        read.HandlerVersion.Should().Be(3);
        read.ContractVersion.Should().Be(ActionInvocation.CurrentContractVersion);
        read.Payload!.Value.GetRawText().Should().Be(original.Payload!.Value.GetRawText(),
            "the publisher stores the handler's payload verbatim and never interprets it");
        read.RequiredCapabilities.Should().Equal(original.RequiredCapabilities);
    }

    [Fact]
    public void AFieldANewerBuildAdded_IsTolerated_ButANewerContractVersionIsRefused()
    {
        const string fromANewerBuild = """
            {"contractVersion":2,"handlerKey":"store.coupon-journey","handlerVersion":1,"somethingNew":true}
            """;

        var invocation = JsonSerializer.Deserialize<ActionInvocation>(fromANewerBuild, Json)!;
        invocation.HandlerKey.Should().Be("store.coupon-journey", "an unknown property must not fail the read");

        var resolution = ActionHandlerResolution.Resolve([new Handler("store.coupon-journey")], invocation);
        resolution.Handler.Should().BeNull("a newer envelope may have changed what its fields mean");
        resolution.Outcome.Status.Should().Be(ActionOutcomeStatus.UnsupportedContract);
        resolution.Outcome.Succeeded.Should().BeFalse();
    }

    [Fact]
    public void AnInvocationWithNoPayloadAndNoCapabilities_ReadsAsEmpty_NotAsDefault()
    {
        var invocation = JsonSerializer.Deserialize<ActionInvocation>(
            """{"handlerKey":"page.open","handlerVersion":1}""", Json)!;

        invocation.Payload.Should().BeNull();
        invocation.ContractVersion.Should().Be(1, "a stored invocation without the field is version 1");
        new Handler("page.open").Capability.Supports(invocation).Status.Should().Be(ActionOutcomeStatus.Ready,
            "an absent capability list requires nothing — it must not fault on an uninitialized array");
    }

    // ── Handler resolution: explicit by key and version, fail closed ────────────────────────────

    [Fact]
    public void ARegisteredHandler_IsChosen_ByKeyAndVersion()
    {
        var wanted = new Handler("store.coupon-journey", 1, 2, ActionCapabilities.PaymentMethodSetup);
        var resolution = ActionHandlerResolution.Resolve(
            [new Handler("page.open"), wanted],
            Invocation(version: 2, required: ActionCapabilities.PaymentMethodSetup));

        resolution.Handler.Should().BeSameAs(wanted);
        resolution.Outcome.Status.Should().Be(ActionOutcomeStatus.Ready);
        resolution.Outcome.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void NoHandlerUnderTheKey_IsUnknownHandler_AndAPortalWithNoHandlersAnswersTheSame()
    {
        ActionHandlerResolution.Resolve([new Handler("page.open")], Invocation())
            .Outcome.Status.Should().Be(ActionOutcomeStatus.UnknownHandler);
        var empty = ActionHandlerResolution.Resolve([], Invocation());
        empty.Outcome.Status.Should().Be(ActionOutcomeStatus.UnknownHandler,
            "a portal that mounts no handler module is a supported configuration with a named answer");
        empty.Handler.Should().BeNull();
        empty.Outcome.HandlerKey.Should().Be("store.coupon-journey", "the outcome names what was asked for");
    }

    [Fact]
    public void AKeyComparison_IsOrdinal_SoALookAlikeKeyIsNotTheHandler()
    {
        ActionHandlerResolution.Resolve([new Handler("Store.Coupon-Journey")], Invocation())
            .Outcome.Status.Should().Be(ActionOutcomeStatus.UnknownHandler);
    }

    [Fact]
    public void AVersionOutsideTheHandlersRange_IsUnsupportedVersion_NamingWhatIsSupported()
    {
        var resolution = ActionHandlerResolution.Resolve(
            [new Handler("store.coupon-journey", 2, 3)], Invocation(version: 1));

        resolution.Handler.Should().BeNull();
        resolution.Outcome.Status.Should().Be(ActionOutcomeStatus.UnsupportedVersion);
        resolution.Outcome.Detail.Should().Contain("2..3");
        resolution.Outcome.HandlerVersion.Should().Be(1);
    }

    [Fact]
    public void AMissingRequiredCapability_FailsPublication_NamingTheCapability()
    {
        var resolution = ActionHandlerResolution.Resolve(
            [new Handler("store.coupon-journey", 1, 1, ActionCapabilities.PaymentMethodSetup)],
            Invocation(required: [ActionCapabilities.PaymentMethodSetup, ActionCapabilities.DeferredSubscription]));

        resolution.Handler.Should().BeNull("an advertised action must not rely on a capability the destination lacks");
        resolution.Outcome.Status.Should().Be(ActionOutcomeStatus.MissingCapability);
        resolution.Outcome.Detail.Should().Be(ActionCapabilities.DeferredSubscription);
    }

    [Fact]
    public void TwoHandlersClaimingOneKeyAndVersion_AtEqualOrder_IsAmbiguous_AndNeitherIsPickedByListOrder()
    {
        var resolution = ActionHandlerResolution.Resolve(
            [new Handler("store.coupon-journey") { Order = 5 }, new Handler("store.coupon-journey") { Order = 5 }],
            Invocation());

        resolution.Handler.Should().BeNull();
        resolution.Outcome.Status.Should().Be(ActionOutcomeStatus.AmbiguousHandler);
        resolution.Outcome.Detail.Should().Contain("order 5");
    }

    [Fact]
    public void TwoHandlersClaimingOneKeyAndVersion_TheLowerOrderWins_WhicheverComesFirstInTheList()
    {
        var first = new Handler("store.coupon-journey") { Order = 10 };
        var second = new Handler("store.coupon-journey") { Order = 20 };

        ActionHandlerResolution.Resolve([first, second], Invocation()).Handler.Should().BeSameAs(first);
        ActionHandlerResolution.Resolve([second, first], Invocation()).Handler.Should().BeSameAs(first,
            "the chain is ordered by the declared Order, never by the order rules were read in");
    }

    [Fact]
    public void ALowerOrderHandlerForAnotherVersion_DoesNotShadowTheOneThatReadsTheVersion()
    {
        var v1First = new Handler("store.coupon-journey", 1, 1) { Order = 1 };
        var v2Later = new Handler("store.coupon-journey", 2, 2) { Order = 50 };

        ActionHandlerResolution.Resolve([v1First, v2Later], Invocation(version: 2)).Handler.Should().BeSameAs(v2Later);
    }

    // ── Rule binding: the chain is rule nodes ───────────────────────────────────────────────────

    /// <summary>The namespaces declared for the pure binding tests — the store package's own.</summary>
    private static readonly ImmutableArray<string> Declared = ["Store"];

    [Fact]
    public void ARule_BindsOneHandlerPerClaimedKey_CarryingItsOrderVersionsAndCapabilities()
    {
        var chain = ActionHandlerRules.Bind(
        [
            ("Store/ActionHandlers/coupon", new ActionHandlerRule
            {
                Claims = ["store.coupon-journey", "store.coupon-preview", "store.coupon-journey"],
                MinVersion = 1,
                MaxVersion = 3,
                Order = 7,
                Handler = "Store/Coupons/Review",
                Capabilities = [ActionCapabilities.PaymentMethodSetup],
                Mutates = true,
            }),
        ], Declared);

        chain.RulesRead.Should().Be(1);
        chain.Rejected.Should().BeEmpty();
        chain.Handlers.Select(h => h.Capability.HandlerKey).OrderBy(k => k, StringComparer.Ordinal).ToArray().Should()
            .Equal(["store.coupon-journey", "store.coupon-preview"], "a duplicate claim binds once");
        var bound = chain.Handlers.First().Capability;
        bound.Order.Should().Be(7);
        bound.MaxVersion.Should().Be(3);
        bound.Mutates.Should().BeTrue();
        bound.Capabilities.Should().Equal(ActionCapabilities.PaymentMethodSetup);
    }

    [Fact]
    public void ADisabledRule_ClaimsNothing_AndIsNotReportedAsBroken()
    {
        var chain = ActionHandlerRules.Bind(
            [("Store/ActionHandlers/coupon", new ActionHandlerRule { Claims = ["k"], Handler = "Store/R", Enabled = false })], Declared);

        chain.Handlers.Should().BeEmpty();
        chain.Rejected.Should().BeEmpty();
        chain.Resolve(Invocation("k")).Outcome.Status.Should().Be(ActionOutcomeStatus.UnknownHandler);
    }

    [Theory]
    [InlineData("", ActionHandlerRules.NoHandler)]
    [InlineData("Elsewhere/Review", ActionHandlerRules.HandlerOutsidePartition)]
    [InlineData("Store/\\evil", ActionHandlerRules.UnsafeHandler)]
    [InlineData("Store/../Admin", ActionHandlerRules.UnsafeHandler)]
    [InlineData("Store/%2e%2e/Admin", ActionHandlerRules.UnsafeHandler)]
    [InlineData("Store/./Review", ActionHandlerRules.UnsafeHandler)]
    [InlineData("Store//Review", ActionHandlerRules.UnsafeHandler)]
    [InlineData("Store/Review?next=/Admin", ActionHandlerRules.UnsafeHandler)]
    [InlineData("Store/Review#/Admin", ActionHandlerRules.UnsafeHandler)]
    public void ABrokenRule_IsRefusedByName_AndTheUnclaimedKeySaysWhichRuleBroke(string handler, string reason)
    {
        var chain = ActionHandlerRules.Bind(
        [
            ("Store/ActionHandlers/coupon", new ActionHandlerRule { Claims = ["store.coupon-journey"], Handler = handler }),
            ("Store/ActionHandlers/unreadable", null),
        ], Declared);

        chain.Handlers.Should().BeEmpty();
        chain.Rejected.Should().Equal(
            new RejectedActionHandlerRule("Store/ActionHandlers/coupon", reason),
            new RejectedActionHandlerRule("Store/ActionHandlers/unreadable", ActionHandlerRules.UnreadableContent));
        var outcome = chain.Resolve(Invocation()).Outcome;
        outcome.Status.Should().Be(ActionOutcomeStatus.UnknownHandler, "a broken rule fails loudly AS unclaimed");
        outcome.Detail.Should().Contain("2 rule(s) read").And.Contain($"Store/ActionHandlers/coupon ({reason})");
    }

    [Fact]
    public void ARuleInAnUndeclaredNamespace_DoesNotWin_EvenWithALowerOrder_AndIsReportedByName()
    {
        var chain = ActionHandlerRules.Bind(
        [
            ("Mallory/ActionHandlers/hijack", new ActionHandlerRule
            {
                Claims = ["store.coupon-journey"], Order = 1, Handler = "Mallory/Phish",
            }),
            ("Store/ActionHandlers/coupon", new ActionHandlerRule
            {
                Claims = ["store.coupon-journey"], Order = 100, Handler = "Store/Coupons/Review",
            }),
        ], Declared);

        chain.Rejected.Should().Equal(
            new RejectedActionHandlerRule("Mallory/ActionHandlers/hijack", ActionHandlerRules.UndeclaredNamespace));
        var resolution = chain.Resolve(Invocation());
        resolution.Outcome.Status.Should().Be(ActionOutcomeStatus.Ready);
        resolution.Handler!.Capability.Order.Should().Be(100,
            "Create rights in some partition are not a declaration: only a declared namespace's rule counts");

        var alone = ActionHandlerRules.Bind([("mallory/ActionHandlers/hijack", new ActionHandlerRule
        {
            Claims = ["store.coupon-journey"], Order = 1, Handler = "mallory/Phish",
        })], Declared);
        var outcome = alone.Resolve(Invocation()).Outcome;
        outcome.Status.Should().Be(ActionOutcomeStatus.UnknownHandler);
        outcome.Detail.Should().Contain($"mallory/ActionHandlers/hijack ({ActionHandlerRules.UndeclaredNamespace})")
            .And.Contain("declared namespaces: Store", "the undeclared rule is named, never dropped in silence");
    }

    [Fact]
    public void DeclaredNamespaces_CompareIgnoringCase()
    {
        var chain = ActionHandlerRules.Bind(
            [("store/ActionHandlers/coupon", new ActionHandlerRule { Claims = ["k"], Handler = "store/Review" })], Declared);

        chain.Rejected.Should().BeEmpty("partitions are case-insensitive, so a declaration is too");
        chain.Resolve(Invocation("k")).Outcome.Status.Should().Be(ActionOutcomeStatus.Ready);
    }

    [Fact]
    public void TheOwnPartitionCheck_ComparesIgnoringCase_AndStillRefusesAnotherPartition()
    {
        var chain = ActionHandlerRules.Bind(
        [
            ("store/ActionHandlers/mixed", new ActionHandlerRule { Claims = ["k.mixed"], Handler = "Store/Review" }),
            ("STORE/ActionHandlers/upper", new ActionHandlerRule { Claims = ["k.upper"], Handler = "store/Review" }),
            ("Store/ActionHandlers/other", new ActionHandlerRule { Claims = ["k.other"], Handler = "Storefront/Review" }),
        ], Declared);

        chain.Resolve(Invocation("k.mixed")).Outcome.Status.Should().Be(ActionOutcomeStatus.Ready,
            "store/… and Store/… address the same partition — partitions are case-insensitive everywhere");
        chain.Resolve(Invocation("k.upper")).Outcome.Status.Should().Be(ActionOutcomeStatus.Ready);
        chain.Rejected.Should().Equal(
            [new RejectedActionHandlerRule("Store/ActionHandlers/other", ActionHandlerRules.HandlerOutsidePartition)],
            "the negative control: ignoring case must not turn into a prefix match on another partition");
    }

    // ── Publisher authorization: fails closed ───────────────────────────────────────────────────

    private static readonly ActionPublicationContext Publication =
        new() { Publisher = "alice", Site = Site, PublisherRequestId = "r1" };

    /// <summary>
    /// A permission seam that grants exactly one (publisher, node, permission) triple and records
    /// what it was asked — the stand-in for <c>hub.CheckPermissionOutcome</c> in the pure binding.
    /// </summary>
    private sealed class OneGrant(string publisher, string node, Permission permission)
    {
        public ImmutableList<(string Node, string Publisher, Permission Permission)> Asked { get; private set; } = [];

        public IObservable<PermissionCheckOutcome> Check(string n, string p, Permission perm)
        {
            Asked = Asked.Add((n, p, perm));
            return Observable.Return(PermissionCheckOutcome.FromVerdict(n == node && p == publisher && perm == permission));
        }
    }

    private static ActionOutcome Validated(ActionHandlerChain chain, ActionPublicationContext context, string key = "k")
    {
        var resolution = chain.Resolve(Invocation(key));
        resolution.Outcome.Status.Should().Be(ActionOutcomeStatus.Ready);
        ActionOutcome? validated = null;
        using (resolution.Handler!.Validate(Invocation(key), context).Subscribe(o => validated = o)) { }
        return validated!;
    }

    [Fact]
    public void ARuleThatStatesNoPublishPermission_AuthorizesNoPublisher()
    {
        var seam = new OneGrant("alice", "Store/Review", Permission.Update);
        var chain = ActionHandlerRules.Bind(
            [("Store/ActionHandlers/r", new ActionHandlerRule { Claims = ["k"], Handler = "Store/Review" })],
            Declared, seam.Check);

        var outcome = Validated(chain, Publication);

        outcome.Status.Should().Be(ActionOutcomeStatus.Unauthorized,
            "a rule-bound handler that does not say who may publish authorizes nobody — never the old structural pass");
        outcome.Detail.Should().Contain("states no publish permission");
        seam.Asked.Should().BeEmpty("there is nothing to check without a stated permission");
    }

    [Theory]
    [InlineData("NotAPermission")]
    [InlineData("None")]
    [InlineData("4")]
    [InlineData("Read, Update")]
    public void APublishPermissionThatIsNotOneFlag_AuthorizesNoPublisher(string word)
    {
        var seam = new OneGrant("alice", "Store/Review", Permission.Update);
        var chain = ActionHandlerRules.Bind(
            [("Store/ActionHandlers/r", new ActionHandlerRule { Claims = ["k"], Handler = "Store/Review", PublishPermission = word })],
            Declared, seam.Check);

        Validated(chain, Publication).Status.Should().Be(ActionOutcomeStatus.Unauthorized);
        seam.Asked.Should().BeEmpty();
    }

    [Fact]
    public void APublisherWithoutTheStatedPermission_IsRefused_AndOneWithIt_IsAdmitted()
    {
        var seam = new OneGrant("alice", "Store/Review", Permission.Update);
        var chain = ActionHandlerRules.Bind(
            [("Store/ActionHandlers/r", new ActionHandlerRule { Claims = ["k"], Handler = "Store/Review", PublishPermission = "update" })],
            Declared, seam.Check);

        var refused = Validated(chain, Publication with { Publisher = "mallory" });
        refused.Status.Should().Be(ActionOutcomeStatus.Unauthorized);
        refused.Detail.Should().Contain("Update").And.Contain("Store/Review");

        Validated(chain, Publication).Status.Should().Be(ActionOutcomeStatus.Ready,
            "the control: the publisher holding Update on the handler node is admitted");
        seam.Asked.Should().Equal(("Store/Review", "mallory", Permission.Update), ("Store/Review", "alice", Permission.Update));
    }

    /// <summary>
    /// A rule may name WHERE its publish permission is checked — a node in a writable space, because a
    /// package's own handler page sits in a partition its one-way sync owns, where no write right can
    /// be granted. The check moves there entirely: Update on the handler node alone no longer admits.
    /// </summary>
    [Fact]
    public void APublishScope_MovesThePublishCheck_OffTheHandlerNode()
    {
        var onScope = new OneGrant("alice", "Campaigns/Publishers", Permission.Update);
        var chain = ActionHandlerRules.Bind(
            [("Store/ActionHandlers/r", new ActionHandlerRule
            {
                Claims = ["k"], Handler = "Store/Review", PublishPermission = "Update", PublishScope = "/Campaigns/Publishers/",
            })],
            Declared, onScope.Check);
        Validated(chain, Publication).Status.Should().Be(ActionOutcomeStatus.Ready, "alice holds Update on the publish scope");
        onScope.Asked.Should().Equal(("Campaigns/Publishers", "alice", Permission.Update));

        var onHandler = new OneGrant("alice", "Store/Review", Permission.Update);
        var moved = ActionHandlerRules.Bind(
            [("Store/ActionHandlers/r", new ActionHandlerRule
            {
                Claims = ["k"], Handler = "Store/Review", PublishPermission = "Update", PublishScope = "Campaigns/Publishers",
            })],
            Declared, onHandler.Check);
        var refused = Validated(moved, Publication);
        refused.Status.Should().Be(ActionOutcomeStatus.Unauthorized, "NEGATIVE CONTROL: Update on the handler node no longer admits");
        refused.Detail.Should().Contain("Campaigns/Publishers");
    }

    [Theory]
    [InlineData("Campaigns/../Admin")]
    [InlineData("Campaigns/%2e%2e")]
    [InlineData("Campaigns//Publishers")]
    [InlineData("Campaigns/Publishers?x=1")]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnsafePublishScope_RefusesTheRuleByName(string scope)
    {
        var chain = ActionHandlerRules.Bind(
            [("Store/ActionHandlers/r", new ActionHandlerRule { Claims = ["k"], Handler = "Store/Review", PublishPermission = "Update", PublishScope = scope })],
            Declared);
        chain.Handlers.Should().BeEmpty();
        chain.Rejected.Should().Equal(new RejectedActionHandlerRule("Store/ActionHandlers/r", ActionHandlerRules.UnsafePublishScope));
    }

    [Theory]
    [InlineData("")]
    [InlineData(WellKnownUsers.System)]
    [InlineData(WellKnownUsers.Anonymous)]
    [InlineData("node/Store/Review")]
    [InlineData("mesh/portal")]
    public void AReservedOrBlankPublisher_IsRefused_BeforeThePermissionSeamIsAsked(string publisher)
    {
        var seam = new OneGrant(publisher, "Store/Review", Permission.Update);
        var chain = ActionHandlerRules.Bind(
            [("Store/ActionHandlers/r", new ActionHandlerRule { Claims = ["k"], Handler = "Store/Review", PublishPermission = "Update" })],
            Declared, seam.Check);

        Validated(chain, Publication with { Publisher = publisher }).Status.Should().Be(ActionOutcomeStatus.Unauthorized,
            "the platform's own identity passes every permission check, so it is never a publisher");
        seam.Asked.Should().BeEmpty();
    }

    [Fact]
    public void APureBindingWithNoPermissionSeam_AuthorizesNoPublisher()
    {
        var chain = ActionHandlerRules.Bind(
            [("Store/ActionHandlers/r", new ActionHandlerRule { Claims = ["k"], Handler = "Store/Review", PublishPermission = "Update" })],
            Declared);

        Validated(chain, Publication).Status.Should().Be(ActionOutcomeStatus.Unauthorized);
    }

    [Fact]
    public void AnUndeterminedPermissionFold_IsFailed_NeverAGrantAndNeverADenial()
    {
        var chain = ActionHandlerRules.Bind(
            [("Store/ActionHandlers/r", new ActionHandlerRule { Claims = ["k"], Handler = "Store/Review", PublishPermission = "Update" })],
            Declared, (_, _, _) => Observable.Return(PermissionCheckOutcome.Undetermined("fold faulted")));

        var outcome = Validated(chain, Publication);
        outcome.Status.Should().Be(ActionOutcomeStatus.Failed);
        outcome.Detail.Should().Contain("could not be checked").And.Contain("fold faulted");
    }

    [Fact]
    public void ARuleBoundHandler_PreparesANavigationToItsHandlerNode_CarryingTheLink()
    {
        var chain = ActionHandlerRules.Bind(
        [
            ("Store/ActionHandlers/coupon", new ActionHandlerRule
            {
                Claims = ["store.coupon-journey"], Handler = "Store/Coupons/Review", Label = "Review the offer",
            }),
        ], Declared);
        var resolution = chain.Resolve(Invocation());
        resolution.Outcome.Status.Should().Be(ActionOutcomeStatus.Ready);

        var context = new ActionRecipientContext { Recipient = "alice", Site = Site, LinkId = "6f1c2d3e" };
        ActionOutcome? prepared = null;
        using (resolution.Handler!.Prepare(Invocation(), context).Subscribe(o => prepared = o)) { }
        var outcome = prepared!;

        outcome.Status.Should().Be(ActionOutcomeStatus.Ready);
        outcome.Navigation!.Target.Should().Be("/Store/Coupons/Review?link=6f1c2d3e");
        outcome.Navigation.Label.Should().Be("Review the offer");
        outcome.Navigation.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void TwoHandlersUnderOneKey_WithDisjointVersionRanges_ResolveByVersion()
    {
        var v1 = new Handler("store.coupon-journey", 1, 1);
        var v2 = new Handler("store.coupon-journey", 2, 2);

        ActionHandlerResolution.Resolve([v1, v2], Invocation(version: 2)).Handler.Should().BeSameAs(v2);
        ActionHandlerResolution.Resolve([v1, v2], Invocation(version: 1)).Handler.Should().BeSameAs(v1);
    }

    [Fact]
    public void TheDefaultPublicationCheck_IsStructural_ThenFailsClosed()
    {
        IActionHandler handler = new Handler("store.coupon-journey");
        var context = new ActionPublicationContext { Publisher = "alice", Site = Site, PublisherRequestId = "r1" };

        ActionOutcome? supported = null, foreign = null;
        using (handler.Validate(Invocation(), context).Subscribe(o => supported = o)) { }
        using (handler.Validate(Invocation(version: 9), context).Subscribe(o => foreign = o)) { }

        supported!.Status.Should().Be(ActionOutcomeStatus.Unauthorized,
            "a handler that states no publisher authorization authorizes nobody — the default never passes a publisher");
        foreign!.Status.Should().Be(ActionOutcomeStatus.UnsupportedVersion,
            "the structural answer still comes first, so the refusal names what does not fit");
    }

    // ── Outcomes: an unknown word is never a success ────────────────────────────────────────────

    [Fact]
    public void OnlyTheKnownSuccessWords_AreSuccess()
    {
        ActionOutcomeStatus.IsSuccess(ActionOutcomeStatus.Ready).Should().BeTrue();
        ActionOutcomeStatus.IsSuccess(ActionOutcomeStatus.Pending).Should().BeTrue();
        ActionOutcomeStatus.IsSuccess(ActionOutcomeStatus.Completed).Should().BeTrue();
        ActionOutcomeStatus.IsSuccess(ActionOutcomeStatus.Revoked).Should().BeFalse();
        ActionOutcomeStatus.IsSuccess("AWordAModuleInvented").Should().BeFalse(
            "an unknown status must never take a branch that means something");
        ActionOutcomeStatus.IsSuccess(null).Should().BeFalse();
        ActionOutcomeStatus.IsSuccess("ready").Should().BeFalse("status words are identifiers, compared exactly");
    }

    [Fact]
    public void AnOutcome_RoundTrips_WithItsNavigationAndItsKeyedReason()
    {
        var outcome = new ActionOutcome
        {
            Status = ActionOutcomeStatus.Ready,
            HandlerKey = "store.coupon-journey",
            HandlerVersion = 1,
            Reason = LocalizableText.Keyed("Your offer is ready.", "sample.offer.ready"),
            Navigation = new ActivityActionReference { Id = "review", Label = "Review offer", Target = "/Store/Claim/abc" },
            ResultPath = "Store/Claim/abc",
        };

        var json = JsonSerializer.Serialize(outcome, Json);
        var read = JsonSerializer.Deserialize<ActionOutcome>(json, Json)!;

        json.Should().NotContain("succeeded", "a computed property must not reach the stored form or a merge patch");
        read.Status.Should().Be(ActionOutcomeStatus.Ready);
        read.Reason!.Key.Should().Be("sample.offer.ready");
        read.Navigation!.Target.Should().Be("/Store/Claim/abc");
        read.ResultPath.Should().Be("Store/Claim/abc");
        read.Succeeded.Should().BeTrue();
    }

    // ── Origin binding ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://portal.example.com", "https://portal.example.com")]
    [InlineData("https://Portal.Example.COM/", "https://portal.example.com")]
    [InlineData("https://portal.example.com:443", "https://portal.example.com")]
    [InlineData("https://portal.example.com:8443", "https://portal.example.com:8443")]
    [InlineData("http://localhost:5022", "http://localhost:5022")]
    public void AnOrigin_IsNormalized(string value, string expected) =>
        ActionSite.NormalizeOrigin(value).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("portal.example.com")]
    [InlineData("//portal.example.com")]
    [InlineData("http://portal.example.com")]
    [InlineData("https://portal.example.com/a/path")]
    [InlineData("https://portal.example.com?next=1")]
    [InlineData("https://portal.example.com#x")]
    [InlineData("https://user@portal.example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://portal.example.com")]
    [InlineData("https://portal.example.com\\@evil.example")]
    [InlineData("https://portal.example.com evil")]
    public void WhatIsNotAnOrigin_IsRefused(string? value) =>
        ActionSite.NormalizeOrigin(value).Should().BeNull();

    [Fact]
    public void AnAction_IsServedOnlyByTheSiteItIsBoundTo()
    {
        Site.IsCanonical.Should().BeTrue();
        Site.IsBoundTo(new ActionSite { SiteId = "portal", Origin = "https://PORTAL.example.com/" })
            .Should().BeTrue("the same site, spelled non-canonically, is the same site");
        Site.IsBoundTo(new ActionSite { SiteId = "portal", Origin = "https://portal.example.org" })
            .Should().BeFalse("a look-alike origin is another site");
        Site.IsBoundTo(new ActionSite { SiteId = "other", Origin = "https://portal.example.com" })
            .Should().BeFalse("a second registration reusing the origin is another site");
        Site.IsBoundTo(null).Should().BeFalse();
        new ActionSite { SiteId = "portal", Origin = "https://portal.example.com/" }.IsCanonical.Should().BeFalse();
    }

    // ── Authenticated recipient context ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(WellKnownUsers.Anonymous)]
    [InlineData("anonymous")]
    [InlineData(WellKnownUsers.Public)]
    [InlineData(WellKnownUsers.System)]
    [InlineData("node/Store/Review")]
    [InlineData("mesh/portal")]
    [InlineData(" node/Store/Review ")]
    public void AVisitorWhoIsNotARealSignedInPerson_IsUnauthenticated(string recipient)
    {
        var context = new ActionRecipientContext { Recipient = recipient, Site = Site, LinkId = "6f1c" };

        context.IsAuthenticated.Should().BeFalse();
        context.Admission(Site).Should().Be(ActionOutcomeStatus.Unauthenticated);
    }

    [Fact]
    public void AnAnonymousVisitorOnTheWrongSite_LearnsOnlyThatTheyMustSignIn()
    {
        var elsewhere = new ActionSite { SiteId = "other", Origin = "https://other.example.com" };
        var anonymous = new ActionRecipientContext { Recipient = WellKnownUsers.Anonymous, Site = elsewhere, LinkId = "6f1c" };

        anonymous.Admission(Site).Should().Be(ActionOutcomeStatus.Unauthenticated,
            "authentication is checked first, so nothing about the link — not even its site — reaches an anonymous visitor");
    }

    [Fact]
    public void ASignedInRecipient_IsAdmittedOnTheBoundSite_AndRefusedOnAnother()
    {
        var elsewhere = new ActionSite { SiteId = "other", Origin = "https://other.example.com" };
        var onSite = new ActionRecipientContext { Recipient = "bob", Site = Site, LinkId = "6f1c" };

        onSite.IsAuthenticated.Should().BeTrue(
            "the control for the reserved-principal cases: a person's id is admitted");
        onSite.Admission(Site).Should().Be(ActionOutcomeStatus.Ready);
        (onSite with { Site = elsewhere }).Admission(Site).Should().Be(ActionOutcomeStatus.OriginMismatch);
    }

    // ── Idempotency ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnIdempotencyKey_IsDeterministic_AndItsPartsCannotRunTogether()
    {
        ActionIdempotency.Key("a", "bc").Should().Be(ActionIdempotency.Key("a", "bc"));
        ActionIdempotency.Key("a", "bc").Should().NotBe(ActionIdempotency.Key("ab", "c"),
            "each part is length-prefixed, so two different part lists cannot share a key");
        ActionIdempotency.Key("a", "").Should().NotBe(ActionIdempotency.Key("a"));
        // Pinned: the key is STORED, so the algorithm is contract. SHA-256 over (len32be ‖ utf8)*,
        // computed independently: printf '\x00\x00\x00\x01a' | shasum -a 256.
        ActionIdempotency.Key("a").Should().Be(
            "72ff6b02949dad95006c343e3db3150090d3afb49f6bbdb92fdc17607997a85c");
    }

    [Fact]
    public void ARecipientsKey_SpansEveryLinkToTheSameOffer_AndSeparatesRecipientsAndSites()
    {
        var first = new ActionRecipientContext { Recipient = "bob", Site = Site, LinkId = "link-1" };
        var second = first with { LinkId = "link-2", DefinitionRevision = 4 };

        first.IdempotencyKey("coupon:SAMPLE").Should().Be(second.IdempotencyKey("coupon:SAMPLE"),
            "printing a second link to the same offer must not give the same person a second claim");
        first.IdempotencyKey("coupon:SAMPLE").Should().NotBe(first.IdempotencyKey("coupon:OTHER"));
        first.IdempotencyKey("coupon:SAMPLE").Should().NotBe(
            (first with { Recipient = "carol" }).IdempotencyKey("coupon:SAMPLE"));
        first.IdempotencyKey("coupon:SAMPLE").Should().NotBe(
            (first with { Site = new ActionSite { SiteId = "other", Origin = "https://other.example.com" } })
            .IdempotencyKey("coupon:SAMPLE"));
    }

    [Fact]
    public void APublication_IsIdempotent_ForItsPublisherAndRequestId()
    {
        var publication = new ActionPublicationContext { Publisher = "alice", Site = Site, PublisherRequestId = "r1" };

        publication.IdempotencyKey.Should().Be((publication with { ActivityPath = "x/_Activity/1" }).IdempotencyKey,
            "a retry from another activity run is the same publication");
        publication.IdempotencyKey.Should().NotBe((publication with { PublisherRequestId = "r2" }).IdempotencyKey);
        publication.IdempotencyKey.Should().NotBe((publication with { Publisher = "bob" }).IdempotencyKey);
    }

    // ── Durable activity action reference ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("/a/6f1c2d3e/card")]
    [InlineData("/Store/Claim/abc?tab=offer")]
    [InlineData("/")]
    [InlineData("/a/b%20c")]
    public void ALocalTarget_IsAccepted(string target) =>
        ActivityActionReference.IsLocalTarget(target).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a/relative/path")]
    [InlineData("https://evil.example/a")]
    [InlineData("//evil.example/a")]
    [InlineData("/\\evil.example")]
    [InlineData("/a\\b")]
    [InlineData("/%2Fevil.example")]
    [InlineData("/%2fevil.example")]
    [InlineData("/%5Cevil.example")]
    [InlineData("/a b")]
    [InlineData("/a\tb")]
    [InlineData("/a%0d%0aSet-Cookie:x")]
    [InlineData("/a%09b")]
    [InlineData("javascript:alert(1)")]
    public void ATargetThatCouldLeaveTheSite_IsRefused(string? target) =>
        ActivityActionReference.IsLocalTarget(target).Should().BeFalse();

    [Fact]
    public void ATargetLongerThanTheLimit_IsRefused() =>
        ActivityActionReference.IsLocalTarget("/" + new string('a', ActivityActionReference.MaxTargetLength))
            .Should().BeFalse();

    [Fact]
    public void AReference_SaysWhyItCannotBeOffered()
    {
        var reference = new ActivityActionReference { Id = "card", Label = "Open QR card", Target = "/a/6f1c/card" };

        reference.Availability().Should().Be(ActivityActionAvailability.Available);
        reference.IsAvailable.Should().BeTrue();
        (reference with { Kind = "RunScript" }).Availability().Should().Be(ActivityActionAvailability.UnsupportedKind,
            "a kind this build does not resolve is reported, never guessed at");
        (reference with { ContractVersion = 2 }).Availability().Should().Be(ActivityActionAvailability.UnsupportedVersion);
        (reference with { ContractVersion = 0 }).Availability().Should().Be(ActivityActionAvailability.UnsupportedVersion);
        (reference with { Target = "https://evil.example" }).Availability().Should().Be(ActivityActionAvailability.UnsafeTarget);
    }

    [Fact]
    public void TheVersion1WireForm_OfAReference_StillReads_AndStoresOnlyPlainData()
    {
        const string stored = """
            {"contractVersion":1,"id":"card","kind":"Navigate","label":"Open QR card",
             "labelKey":"sample.openCard","target":"/a/6f1c/card","resultPath":"Ops/Links/6f1c",
             "handlerKey":"store.coupon-journey","handlerVersion":1}
            """;

        var reference = JsonSerializer.Deserialize<ActivityActionReference>(stored, Json)!;

        reference.Id.Should().Be("card");
        reference.LabelKey.Should().Be("sample.openCard");
        reference.ResultPath.Should().Be("Ops/Links/6f1c");
        reference.HandlerKey.Should().Be("store.coupon-journey");
        reference.IsAvailable.Should().BeTrue();

        using var written = JsonDocument.Parse(JsonSerializer.Serialize(reference, Json));
        written.RootElement.EnumerateObject()
            .Should().OnlyContain(p => p.Value.ValueKind == JsonValueKind.String || p.Value.ValueKind == JsonValueKind.Number,
                "a stored reference is strings and numbers — never a control graph that needs a live hub to revive");
        written.RootElement.TryGetProperty("isAvailable", out _).Should().BeFalse(
            "a computed property must not reach the stored form or a merge patch");
    }

    [Fact]
    public void AnActivityWrittenBeforeTheFieldExisted_StillReads_WithNoActions()
    {
        const string old = """
            {"category":"ScriptExecution","id":"abc","status":"Succeeded",
             "messages":[],"returnValue":{"exported":3},"hubPath":"TestData"}
            """;

        var log = JsonSerializer.Deserialize<ActivityLog>(old, WithEnumNames())!;

        log.Actions.Should().BeEmpty("an old activity offers no durable action, and reads without one");
        log.Status.Should().Be(ActivityStatus.Succeeded);
        log.ReturnValue!.Value.GetProperty("exported").GetInt32().Should().Be(3,
            "the existing return value is untouched by the new field");
    }

    [Fact]
    public void AnActivityWithActions_RoundTrips()
    {
        var log = new ActivityLog("ScriptExecution") { Status = ActivityStatus.Succeeded }
            .WithAction(new ActivityActionReference { Id = "card", Label = "Open QR card", Target = "/a/6f1c/card" });

        var read = JsonSerializer.Deserialize<ActivityLog>(JsonSerializer.Serialize(log, WithEnumNames()), WithEnumNames())!;

        read.Actions.Should().ContainSingle();
        read.Actions[0].Target.Should().Be("/a/6f1c/card");
        read.Actions[0].IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void RecordingTheSameActionTwice_LeavesOneEntry_AndKeepsItsPlace()
    {
        var first = new ActivityActionReference { Id = "card", Label = "Open QR card", Target = "/a/1/card" };
        var other = new ActivityActionReference { Id = "doc", Label = "Open document", Target = "/Docs/1" };

        var log = new ActivityLog("ScriptExecution")
            .WithAction(first)
            .WithAction(other)
            .WithAction(first with { Target = "/a/2/card" });

        log.Actions.Select(a => a.Id).Should().Equal("card", "doc");
        log.Actions[0].Target.Should().Be("/a/2/card", "a retried publication replaces its own entry");
    }

    private static JsonSerializerOptions WithEnumNames()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return options;
    }
}
