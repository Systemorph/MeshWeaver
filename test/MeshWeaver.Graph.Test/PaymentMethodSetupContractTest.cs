#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Payments;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the payment-method SETUP and deferred-renewal contract (MeshWeaver#4693): saving a method
/// and authorizing a renewal are two consents, a setup is never a paid checkout, a zero invoice is
/// never a paid period, a provider fact must match the request it is offered for, and secrets do
/// not reach a serialized form.
///
/// <para>No provider is called: every case is the pure contract, exercised with a provider written
/// in this file.</para>
/// </summary>
public class PaymentMethodSetupContractTest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset BenefitEnd = new(2026, 12, 30, 12, 0, 0, TimeSpan.Zero);

    private static PaymentConsentReference Storage() => new()
    {
        Kind = PaymentConsentKind.MethodStorage,
        TextVersion = "storage-v1",
        TextHash = "h-storage",
        AcceptedAt = Now,
    };

    private static PaymentConsentReference Renewal(string termsHash = "terms-1") => new()
    {
        Kind = PaymentConsentKind.RecurringRenewal,
        TextVersion = "renewal-v1",
        TextHash = "h-renewal",
        AcceptedAt = Now,
        TermsHash = termsHash,
    };

    private static PaymentMethodSetupRequest SetupOnly() => new()
    {
        Reference = "Ops/Setups/s1",
        Buyer = "alice",
        ClaimReference = "Ops/Claims/c1",
        SiteId = "portal",
        Currency = "CHF",
        IntendedUse = PaymentMethodIntendedUse.CustomerPresentPurchaseOnly,
        StorageConsent = Storage(),
        TermsHash = "terms-1",
        IdempotencyKey = "idem-1",
        ReturnUrl = "https://portal.example.com/Store/Claim/c1",
        ExpectedMode = "test",
    };

    private static PaymentMethodSetupRequest WithRenewal() => SetupOnly() with
    {
        IntendedUse = PaymentMethodIntendedUse.AuthorizedRenewal,
        RenewalConsent = Renewal(),
    };

    private static PaymentMethodSetupSession Session() => new()
    {
        SetupId = "seti_1",
        Handoff = PaymentSetupHandoff.ClientRendered,
        ClientSecret = "seti_1_secret_abc",
        ClientPublicKey = "pk_test_1",
        CustomerId = "cus_1",
        Mode = "test",
    };

    private static PaymentMethodSetupFact Succeeded(PaymentMethodSetupRequest request) => new()
    {
        Status = PaymentSetupStatus.Succeeded,
        SetupId = "seti_1",
        CustomerId = "cus_1",
        PaymentMethodId = "pm_1",
        Summary = new PaymentMethodSummary { Kind = PaymentMethodKind.Card, Brand = "visa", Last4 = "4242" },
        OffSessionAuthorized = request.AuthorizesOffSession,
        Mode = "test",
        EventId = "evt_1",
        Metadata = request.StampedMetadata(),
    };

    private static DeferredSubscriptionRequest RenewalRequest() => new()
    {
        ClaimReference = "Ops/Claims/c1",
        Buyer = "alice",
        SetupId = "seti_1",
        CustomerId = "cus_1",
        PaymentMethodId = "pm_1",
        PlanTier = "personal",
        Cadence = "monthly",
        Currency = "CHF",
        RecurringTotal = 19.00m,
        FirstChargeAt = BenefitEnd,
        RenewalConsent = Renewal(),
        TermsHash = "terms-1",
        IdempotencyKey = "idem-sub-1",
        ExpectedMode = "test",
    };

    private static DeferredSubscriptionFact Scheduled(DeferredSubscriptionRequest request) => new()
    {
        Status = DeferredSubscriptionStatus.Scheduled,
        SubscriptionId = "sub_1",
        CustomerId = request.CustomerId,
        PaymentMethodId = request.PaymentMethodId,
        PlanTier = request.PlanTier,
        Cadence = request.Cadence,
        Currency = "chf",
        RecurringTotal = request.RecurringTotal,
        FirstChargeAt = request.FirstChargeAt,
        InitialInvoiceId = "in_0",
        InitialInvoiceAmountDue = 0m,
        AmountCharged = 0m,
        Mode = "test",
        Metadata = request.StampedMetadata(),
    };

    // ── Two consents, never one ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SavingAMethod_AuthorizesNoOffSessionUse()
    {
        var request = SetupOnly();

        request.Problems().Should().BeEmpty();
        request.AuthorizesOffSession.Should().BeFalse(
            "storage consent alone authorizes neither a purchase nor a subscription");
    }

    [Fact]
    public void ARenewalUse_WithoutASeparateRenewalConsent_IsRefused_AndAuthorizesNothing()
    {
        var request = SetupOnly() with { IntendedUse = PaymentMethodIntendedUse.AuthorizedRenewal };

        request.AuthorizesOffSession.Should().BeFalse("the use word alone is not a mandate");
        request.Problems().Should().Contain(PaymentRequestProblem.MissingRenewalConsent);
    }

    [Fact]
    public void AStorageConsentOfferedAsTheRenewalConsent_DoesNotAuthorizeARenewal()
    {
        var request = SetupOnly() with
        {
            IntendedUse = PaymentMethodIntendedUse.AuthorizedRenewal,
            RenewalConsent = Storage() with { TermsHash = "terms-1" },
        };

        request.AuthorizesOffSession.Should().BeFalse("a recurring mandate is never inferred from a storage consent");
        request.Problems().Should().Contain(PaymentRequestProblem.MissingRenewalConsent);
    }

    [Fact]
    public void ARenewalConsentOnACustomerPresentSetup_IsRefused_SoACheckboxCannotUpgradeIt()
    {
        var request = SetupOnly() with { RenewalConsent = Renewal() };

        request.AuthorizesOffSession.Should().BeFalse();
        request.Problems().Should().Contain(PaymentRequestProblem.RenewalConsentWithoutRenewalUse);
    }

    [Fact]
    public void ARenewalConsentGivenAgainstOtherTerms_IsRefused()
    {
        var request = WithRenewal() with { RenewalConsent = Renewal("terms-OLD") };

        request.Problems().Should().Contain(PaymentRequestProblem.ConsentTermsMismatch,
            "a changed price or date invalidates the earlier acceptance");
    }

    [Fact]
    public void AnExplicitRenewal_WithItsOwnConsent_AuthorizesOffSessionUse()
    {
        var request = WithRenewal();

        request.Problems().Should().BeEmpty();
        request.AuthorizesOffSession.Should().BeTrue();
    }

    [Fact]
    public void ASetupWithoutStorageConsent_BuyerOrIdempotencyKey_IsRefused()
    {
        var request = SetupOnly() with
        {
            Buyer = " ",
            IdempotencyKey = "",
            StorageConsent = Renewal(),
        };

        request.Problems().Should().Contain(PaymentRequestProblem.MissingBuyer)
            .And.Contain(PaymentRequestProblem.MissingIdempotencyKey)
            .And.Contain(PaymentRequestProblem.MissingStorageConsent);
    }

    // ── A verified fact must be THIS request's own ──────────────────────────────────────────────

    [Fact]
    public void ASucceededFact_ThatMatches_SatisfiesTheRequest()
    {
        var request = SetupOnly();
        var fact = Succeeded(request);

        fact.Mismatches(request, Session()).Should().BeEmpty();
        fact.Satisfies(request, Session()).Should().BeTrue();
        fact.Buyer.Should().Be("alice");
        fact.ClaimReference.Should().Be("Ops/Claims/c1");
        fact.IntendedUse.Should().Be(PaymentMethodIntendedUse.CustomerPresentPurchaseOnly);
    }

    [Fact]
    public void AFactThatIsNotSucceeded_SatisfiesNothing_HoweverWellItMatches()
    {
        var request = SetupOnly();

        foreach (var status in new[]
                 {
                     PaymentSetupStatus.Pending, PaymentSetupStatus.RequiresAction, PaymentSetupStatus.Failed,
                     PaymentSetupStatus.Cancelled, PaymentSetupStatus.Expired, "AWordTheProviderInvented", "succeeded",
                 })
        {
            var fact = Succeeded(request) with { Status = status };
            fact.Mismatches(request, Session()).Should().BeEmpty("negative control: only the status differs");
            fact.Satisfies(request, Session()).Should().BeFalse($"'{status}' is not a saved method");
        }
    }

    [Fact]
    public void AFactForAnotherBuyerClaimSetupCustomerOrMode_IsAMismatch_NamedByField()
    {
        var request = SetupOnly();
        var good = Succeeded(request);

        (good with { Metadata = good.Metadata.SetItem(PaymentMetadata.Buyer, "mallory") })
            .Mismatches(request, Session()).Should().Equal(nameof(PaymentMethodSetupFact.Buyer));
        (good with { Metadata = good.Metadata.SetItem(PaymentMetadata.ClaimReference, "Ops/Claims/other") })
            .Mismatches(request, Session()).Should().Equal(nameof(PaymentMethodSetupFact.ClaimReference));
        (good with { Metadata = good.Metadata.SetItem(PaymentMetadata.TermsHash, "terms-OLD") })
            .Mismatches(request, Session()).Should().Equal(nameof(PaymentMethodSetupFact.TermsHash));
        (good with { SetupId = "seti_OTHER" })
            .Mismatches(request, Session()).Should().Equal(nameof(PaymentMethodSetupFact.SetupId));
        (good with { CustomerId = "cus_OTHER" })
            .Mismatches(request, Session()).Should().Equal(nameof(PaymentMethodSetupFact.CustomerId));
        (good with { Mode = "live" })
            .Mismatches(request, Session()).Should().Equal(nameof(PaymentMethodSetupFact.Mode));
        (good with { Mode = "TEST" })
            .Mismatches(request, Session()).Should().BeEmpty("the mode is compared case-insensitively");
    }

    [Fact]
    public void OffSessionPermissionTheRequestNeverAskedFor_IsAMismatch_NotABonus()
    {
        var request = SetupOnly();
        var fact = Succeeded(request) with { OffSessionAuthorized = true };

        fact.Mismatches(request, Session()).Should().Equal(nameof(PaymentMethodSetupFact.OffSessionAuthorized));
        fact.Satisfies(request, Session()).Should().BeFalse();

        var renewal = WithRenewal();
        (Succeeded(renewal) with { OffSessionAuthorized = false }).Satisfies(renewal, Session())
            .Should().BeFalse("a renewal needs the permission the buyer granted — its absence is a mismatch too");
        Succeeded(renewal).Satisfies(renewal, Session()).Should().BeTrue();
    }

    [Fact]
    public void AFactWithNoMetadataAtAll_MatchesNoRequest()
    {
        var request = SetupOnly();
        var bare = new PaymentMethodSetupFact { Status = PaymentSetupStatus.Succeeded, SetupId = "seti_1", Mode = "test" };

        bare.Satisfies(request, Session()).Should().BeFalse(
            "a succeeded setup that names nobody is somebody else's, or nobody's");
        bare.NamesWork.Should().BeFalse();
    }

    // ── Secrets and stored forms ────────────────────────────────────────────────────────────────

    [Fact]
    public void ASession_NeverCarriesItsClientSecret_IntoAPrintOrASerializedForm()
    {
        var session = Session();
        session.ClientSecret.Should().Be("seti_1_secret_abc", "negative control: the secret IS on the record");

        session.ToString().Should().NotContain("seti_1_secret_abc").And.Contain("(redacted)").And.Contain("seti_1");
        var json = JsonSerializer.Serialize(session, Json);
        json.Should().NotContain("seti_1_secret_abc").And.NotContain("clientSecret")
            .And.Contain("pk_test_1", "negative control: the rest of the session, including the PUBLIC key, is serialized");
    }

    [Fact]
    public void AFact_RoundTrips_AndItsComputedAccessorsAreNotStored()
    {
        var fact = Succeeded(WithRenewal());

        var json = JsonSerializer.Serialize(fact, Json);
        var read = JsonSerializer.Deserialize<PaymentMethodSetupFact>(json, Json)!;

        json.Should().NotContain("\"isSucceeded\"").And.NotContain("\"namesWork\"").And.NotContain("\"buyer\":null");
        read.Status.Should().Be(PaymentSetupStatus.Succeeded);
        read.PaymentMethodId.Should().Be("pm_1");
        read.Summary!.Last4.Should().Be("4242");
        read.OffSessionAuthorized.Should().BeTrue();
        read.ClaimReference.Should().Be("Ops/Claims/c1");
        read.Satisfies(WithRenewal(), Session()).Should().BeTrue("a stored fact still answers for its request");
    }

    [Fact]
    public void TheStampedMetadata_UsesTheSharedKeys_SoTheReaderFindsWhatTheWriterStamped()
    {
        var stamped = WithRenewal().StampedMetadata();

        stamped[PaymentMetadata.Buyer].Should().Be("alice");
        stamped[PaymentMetadata.SetupReference].Should().Be("Ops/Setups/s1");
        stamped[PaymentMetadata.ClaimReference].Should().Be("Ops/Claims/c1");
        stamped[PaymentMetadata.TermsHash].Should().Be("terms-1");
        stamped[PaymentMetadata.SiteId].Should().Be("portal");
        stamped[PaymentMetadata.IntendedUse].Should().Be(PaymentMethodIntendedUse.AuthorizedRenewal);
        stamped.ContainsKey(PaymentMetadata.OrderPath).Should().BeFalse(
            "a setup stamps no order — nothing about it can be read as a purchase");
        // The key strings are on the wire at the processor: pinned.
        PaymentMetadata.ClaimReference.Should().Be("claimReference");
        PaymentMetadata.SetupReference.Should().Be("setupReference");
        PaymentMetadata.TermsHash.Should().Be("termsHash");
        PaymentMetadata.IntendedUse.Should().Be("intendedUse");
        PaymentMetadata.SiteId.Should().Be("siteId");
    }

    // ── A setup is not a checkout; a zero invoice is not a period ───────────────────────────────

    [Fact]
    public void ASetupDelivery_NamesWork_WithoutBeingACheckout()
    {
        var delivery = new PaymentDelivery { CanAuthenticate = true, Authentic = true, Setup = Succeeded(SetupOnly()) };

        delivery.Checkout.Should().BeNull("a finished setup is never handed to paid-order fulfilment");
        delivery.Subscription.Should().BeNull();
        delivery.NamesWork.Should().BeTrue("a setup naming its claim is consequential work");
        (delivery with { Authentic = false }).NamesWork.Should().BeTrue(
            "an unverifiable setup delivery is retained as evidence, like an unverifiable paid order");
        new PaymentDelivery { CanAuthenticate = true, Authentic = false, Setup = new PaymentMethodSetupFact { Status = PaymentSetupStatus.Succeeded, SetupId = "seti_x" } }
            .NamesWork.Should().BeFalse("…while one that names nothing is junk and may be discarded");
        PaymentDelivery.None.NamesWork.Should().BeFalse("the pre-existing answer for an empty delivery is unchanged");
    }

    [Fact]
    public void ADeferredSubscriptionDelivery_NamesWork_AndIsNotARenewal()
    {
        var delivery = new PaymentDelivery
        {
            CanAuthenticate = true,
            Authentic = true,
            DeferredSubscription = Scheduled(RenewalRequest()),
        };

        delivery.NamesWork.Should().BeTrue();
        delivery.Subscription.Should().BeNull("scheduling a renewal renews nothing");
        delivery.Checkout.Should().BeNull();
    }

    [Fact]
    public void AZeroInvoice_IsNeverAPaidRenewal_AndAPositiveOrUnstatedOneStillIs()
    {
        var metadata = RenewalRequest().StampedMetadata();
        var zero = new PaymentSubscriptionEvent(PaymentSubscriptionChange.Renewed, "sub_1", metadata) { AmountPaid = 0m };
        var paid = zero with { AmountPaid = 19.00m, PaidThrough = BenefitEnd.AddMonths(1) };
        var unstated = zero with { AmountPaid = null };

        zero.Renews.Should().BeTrue("negative control: the event claims to renew");
        zero.IsPaidRenewal.Should().BeFalse("a zero invoice pays for no period, whatever it is called");
        paid.IsPaidRenewal.Should().BeTrue("the first VERIFIED paid renewal starts paid-period accounting");
        unstated.IsPaidRenewal.Should().BeTrue(
            "a provider written before the amount was reported keeps its existing meaning");
        new PaymentSubscriptionEvent(PaymentSubscriptionChange.Ended, "sub_1", metadata) { AmountPaid = 19m }
            .IsPaidRenewal.Should().BeFalse();
        paid.ClaimReference.Should().Be("Ops/Claims/c1");
        paid.PlanTier.Should().Be("personal");
        paid.Buyer.Should().Be("alice");
    }

    // ── Deferred subscription: exact terms, zero due, proven ────────────────────────────────────

    [Fact]
    public void ARenewalRequest_WithCompleteTerms_MayBeSent_AndAsksForNothingToday()
    {
        RenewalRequest().Problems(Now).Should().BeEmpty();
        DeferredSubscriptionRequest.DueToday.Should().Be(0m);
    }

    [Fact]
    public void ARenewalRequest_IsRefused_ForEachMissingOrContradictoryTerm()
    {
        var request = RenewalRequest();

        (request with { RenewalConsent = Storage() }).Problems(Now)
            .Should().Contain(PaymentRequestProblem.MissingRenewalConsent);
        (request with { TermsHash = "terms-NEW" }).Problems(Now)
            .Should().Contain(PaymentRequestProblem.ConsentTermsMismatch, "a new quote needs a new consent");
        (request with { RecurringTotal = 0m }).Problems(Now)
            .Should().Contain(PaymentRequestProblem.NonPositiveRecurringTotal);
        (request with { FirstChargeAt = Now }).Problems(Now)
            .Should().Contain(PaymentRequestProblem.FirstChargeNotInFuture);
        (request with { PaymentMethodId = "" }).Problems(Now)
            .Should().Contain(PaymentRequestProblem.MissingPaymentMethod);
        (request with { IdempotencyKey = "" }).Problems(Now)
            .Should().Contain(PaymentRequestProblem.MissingIdempotencyKey);
    }

    [Fact]
    public void AScheduledSubscription_ThatMatches_SatisfiesTheRequest()
    {
        var request = RenewalRequest();
        var fact = Scheduled(request);

        fact.Mismatches(request).Should().BeEmpty();
        fact.IsZeroDueToday.Should().BeTrue();
        fact.Satisfies(request).Should().BeTrue();
    }

    [Fact]
    public void ZeroDue_MustBeProven_AnUnreportedAmountIsNotAZero()
    {
        var request = RenewalRequest();
        var good = Scheduled(request);

        (good with { InitialInvoiceAmountDue = null }).IsZeroDueToday.Should().BeFalse();
        (good with { AmountCharged = null }).IsZeroDueToday.Should().BeFalse();
        (good with { InitialInvoiceAmountDue = 0.50m }).Satisfies(request).Should().BeFalse(
            "a positive amount due today is a failed scheduling attempt, never a success to explain away");
        (good with { AmountCharged = 19.00m }).Mismatches(request)
            .Should().Equal(nameof(DeferredSubscriptionFact.IsZeroDueToday));
    }

    [Fact]
    public void AChangedDateTotalCadencePlanOrOwner_IsAMismatch_NamedByField()
    {
        var request = RenewalRequest();
        var good = Scheduled(request);

        (good with { FirstChargeAt = BenefitEnd.AddDays(-1) }).Mismatches(request)
            .Should().Equal(nameof(DeferredSubscriptionFact.FirstChargeAt));
        (good with { FirstChargeAt = null }).Mismatches(request)
            .Should().Equal(nameof(DeferredSubscriptionFact.FirstChargeAt));
        (good with { RecurringTotal = 29.00m }).Mismatches(request)
            .Should().Equal(nameof(DeferredSubscriptionFact.RecurringTotal));
        (good with { Cadence = "annual" }).Mismatches(request)
            .Should().Equal(nameof(DeferredSubscriptionFact.Cadence));
        (good with { PlanTier = "team" }).Mismatches(request)
            .Should().Equal(nameof(DeferredSubscriptionFact.PlanTier));
        (good with { Currency = "EUR" }).Mismatches(request)
            .Should().Equal(nameof(DeferredSubscriptionFact.Currency));
        (good with { Metadata = good.Metadata.SetItem(PaymentMetadata.ClaimReference, "Ops/Claims/other") })
            .Mismatches(request).Should().Equal(nameof(DeferredSubscriptionFact.ClaimReference));
        (good with { PaymentMethodId = "pm_OTHER" }).Mismatches(request)
            .Should().Equal(nameof(DeferredSubscriptionFact.PaymentMethodId));
    }

    [Fact]
    public void OnlyScheduled_IsScheduled_AndAnUnknownFateIsNotSuccess()
    {
        var request = RenewalRequest();

        foreach (var status in new[]
                 {
                     DeferredSubscriptionStatus.Indeterminate, DeferredSubscriptionStatus.Refused,
                     DeferredSubscriptionStatus.RequiresAction, DeferredSubscriptionStatus.Cancelled,
                     DeferredSubscriptionStatus.NotSupported, "AWordTheProviderInvented",
                 })
            (Scheduled(request) with { Status = status }).Satisfies(request)
                .Should().BeFalse($"'{status}' is not a scheduled renewal");

        (Scheduled(request) with { SubscriptionId = null }).Satisfies(request)
            .Should().BeFalse("a schedule with no subscription behind it is not one");
    }

    // ── The optional capability ─────────────────────────────────────────────────────────────────

    /// <summary>A provider that saves methods and implements nothing optional.</summary>
    private sealed class SetupOnlyProvider : IPaymentMethodSetupProvider
    {
        public string DisplayName => "Sample";
        public string? SetupUnavailable => null;

        public IObservable<PaymentMethodSetupCapabilities> DescribeSetup(string intendedUse, string currency) =>
            Observable.Return(new PaymentMethodSetupCapabilities
            {
                IntendedUse = intendedUse,
                Currency = currency,
                Methods =
                [
                    new PaymentMethodCapability { Kind = PaymentMethodKind.Card, Reuse = PaymentMethodReuse.Reusable },
                    new PaymentMethodCapability
                    {
                        Kind = PaymentMethodKind.ApplePay,
                        Reuse = intendedUse == PaymentMethodIntendedUse.AuthorizedRenewal
                            ? PaymentMethodReuse.Reusable
                            : PaymentMethodReuse.FreshAuthorizationEachPurchase,
                    },
                ],
                Handoffs = [PaymentSetupHandoff.Hosted],
            });

        public IObservable<PaymentMethodSetupSession> CreateSetup(PaymentMethodSetupRequest request) =>
            Observable.Return(Session());

        public IObservable<PaymentMethodSetupFact> RetrieveSetup(string setupId) =>
            Observable.Return(new PaymentMethodSetupFact { Status = PaymentSetupStatus.Pending, SetupId = setupId });
    }

    [Fact]
    public void AProviderThatOnlySavesMethods_OffersNoRenewal_AndAnswersNotSupported_NeverAThrow()
    {
        IPaymentMethodSetupProvider provider = new SetupOnlyProvider();
        provider.SupportsDeferredSubscription.Should().BeFalse();

        DeferredSubscriptionFact? created = null, read = null, cancelled = null;
        using (provider.CreateDeferredSubscription(RenewalRequest()).Subscribe(f => created = f)) { }
        using (provider.RetrieveDeferredSubscription("sub_1", null).Subscribe(f => read = f)) { }
        using (provider.CancelDeferredSubscription("sub_1").Subscribe(f => cancelled = f)) { }

        created!.Status.Should().Be(DeferredSubscriptionStatus.NotSupported);
        created.Satisfies(RenewalRequest()).Should().BeFalse("no renewal was scheduled");
        read!.Status.Should().Be(DeferredSubscriptionStatus.NotSupported);
        cancelled!.Status.Should().Be(DeferredSubscriptionStatus.NotSupported,
            "a caller must not read 'billing is off' from a provider that never scheduled anything");
    }

    /// <summary>
    /// 🚨 A provider that does not hand out client secrets says so, and refuses loudly if asked anyway —
    /// an existing provider gains no way to leak one by this contract growing.
    /// </summary>
    [Fact]
    public void AProviderThatDoesNotRevealClientSecrets_SaysSo_AndRefusesLoudlyIfAskedAnyway()
    {
        IPaymentMethodSetupProvider provider = new SetupOnlyProvider();
        provider.RevealsClientSecret.Should().BeFalse();

        Exception? error = null;
        var emitted = false;
        using (provider.RevealClientSecret("seti_1", "alice").Subscribe(_ => emitted = true, e => error = e)) { }

        emitted.Should().BeFalse();
        error.Should().BeOfType<NotSupportedException>();
        error!.Message.Should().Contain("Sample");
    }

    /// <summary>🚨 A revealed client secret never reaches a serialized form or a log line.</summary>
    [Fact]
    public void ARevealedClientSecret_IsNeverSerialized_NorPrinted()
    {
        var revealed = new PaymentSetupClientSecret { SetupId = "seti_1", Secret = "seti_1_secret_abc", ClientPublicKey = "pk_test_1" };
        var json = JsonSerializer.Serialize(revealed, Json);
        json.Should().NotContain("seti_1_secret_abc");
        json.Should().Contain("pk_test_1", "the publishable key is public by nature");
        revealed.ToString().Should().NotContain("seti_1_secret_abc");
        revealed.ToString().Should().Contain("(redacted)");
    }

    [Fact]
    public void AProviderThatDoesNotRemoveMethods_SaysSo_AndRefusesLoudlyIfAskedAnyway()
    {
        IPaymentMethodSetupProvider provider = new SetupOnlyProvider();
        provider.RemovesPaymentMethods.Should().BeFalse();

        Exception? error = null;
        var emitted = false;
        using (provider.RemovePaymentMethod("cus_1", "pm_1").Subscribe(_ => emitted = true, e => error = e)) { }

        emitted.Should().BeFalse();
        error.Should().BeOfType<NotSupportedException>();
        error!.Message.Should().Contain("Sample");
    }

    [Fact]
    public void TheDefaultCancelOfASetup_ReportsWhatTheProcessorHolds_NotAnAssumedCancellation()
    {
        IPaymentMethodSetupProvider provider = new SetupOnlyProvider();

        PaymentMethodSetupFact? fact = null;
        using (provider.CancelSetup("seti_1").Subscribe(f => fact = f)) { }

        fact!.SetupId.Should().Be("seti_1");
        fact.Status.Should().Be(PaymentSetupStatus.Pending,
            "the default changes nothing, so it must not claim the setup is cancelled");
    }

    [Fact]
    public void WalletReuse_IsPurposeSpecific_AndAnUnlistedKindIsNeverAssumedReusable()
    {
        IPaymentMethodSetupProvider provider = new SetupOnlyProvider();

        PaymentMethodSetupCapabilities? present = null, renewal = null;
        using (provider.DescribeSetup(PaymentMethodIntendedUse.CustomerPresentPurchaseOnly, "CHF").Subscribe(c => present = c)) { }
        using (provider.DescribeSetup(PaymentMethodIntendedUse.AuthorizedRenewal, "CHF").Subscribe(c => renewal = c)) { }

        present!.ReuseOf(PaymentMethodKind.Card).Should().Be(PaymentMethodReuse.Reusable);
        present.ReuseOf(PaymentMethodKind.ApplePay).Should().Be(PaymentMethodReuse.FreshAuthorizationEachPurchase,
            "a wallet that can pay is not thereby a stored method for a customer-present purchase");
        renewal!.ReuseOf(PaymentMethodKind.ApplePay).Should().Be(PaymentMethodReuse.Reusable);
        present.ReuseOf(PaymentMethodKind.GooglePay).Should().Be(PaymentMethodReuse.Unverified,
            "a kind the provider did not list is unknown, and unknown is not reusable");
        new PaymentMethodSetupCapabilities { IntendedUse = "x", Currency = "CHF" }
            .ReuseOf(PaymentMethodKind.Card).Should().Be(PaymentMethodReuse.Unverified);
    }

    /// <summary>
    /// The existing provider contract is untouched: a provider written before this change — one
    /// that implements <see cref="IPaymentProvider"/> only — still compiles and is not a setup
    /// provider.
    /// </summary>
    private sealed class CheckoutOnlyProvider : IPaymentProvider
    {
        public string DisplayName => "Older";
        public string? Unavailable => null;
        public bool Sells => true;
        public string SecretSettingName => "Older:Secret";
        public string WebhookSecretSettingName => "Older:WebhookSecret";
        public string DeliverySignatureHeader => "Older-Signature";
        public IObservable<PaymentCheckout> CreateCheckout(PaymentCheckoutRequest request) =>
            Observable.Empty<PaymentCheckout>();
        public IObservable<Unit> CancelAtPeriodEnd(string subscriptionId) => Observable.Empty<Unit>();
        public PaymentDelivery ReadDelivery(string? signatureHeader, string body, DateTimeOffset now) =>
            PaymentDelivery.None;
        public IObservable<PaymentDeliveryPath> DescribeDeliveryPath() =>
            Observable.Empty<PaymentDeliveryPath>();
    }

    [Fact]
    public void AnExistingCheckoutProvider_IsUnchanged_AndIsNotASetupProvider()
    {
        object provider = new CheckoutOnlyProvider();

        (provider is IPaymentMethodSetupProvider).Should().BeFalse(
            "setup is a separate optional capability — selling does not imply it");
        var delivery = ((IPaymentProvider)provider).ReadDelivery(null, "{}", Now);
        delivery.Setup.Should().BeNull();
        delivery.DeferredSubscription.Should().BeNull();
        delivery.NamesWork.Should().BeFalse();
    }

    [Fact]
    public void AnOldCheckoutDelivery_StillNamesWork_ExactlyAsBefore()
    {
        var checkout = new PaymentCheckoutEvent(PaymentCheckoutOutcome.Completed, "cs_1",
            ImmutableDictionary<string, string>.Empty.Add(PaymentMetadata.OrderPath, "Orders/o1"));

        new PaymentDelivery { CanAuthenticate = true, Authentic = true, Checkout = checkout }
            .NamesWork.Should().BeTrue();
    }

    // ── Every field a fact is matched on is required, and checked ──────────────────────────────

    [Fact]
    public void AConsentWithNoAcceptanceInstant_RecordsNoAcceptance()
    {
        Storage().Is(PaymentConsentKind.MethodStorage).Should().BeTrue("negative control: a stamped consent counts");
        var request = WithRenewal() with { StorageConsent = Storage() with { AcceptedAt = default } };

        request.Problems().Should().Contain(PaymentRequestProblem.MissingStorageConsent);
        (WithRenewal() with { RenewalConsent = Renewal() with { AcceptedAt = default } })
            .AuthorizesOffSession.Should().BeFalse("an off-session mandate needs a recorded acceptance");
    }

    [Fact]
    public void ASetupWithABlankReferenceCurrencyUseOrReturnUrl_IsRefused()
    {
        SetupOnly().Problems().Should().BeEmpty("negative control");
        var request = SetupOnly() with { Reference = " ", Currency = "", IntendedUse = " ", ReturnUrl = "" };

        request.Problems().Should().Contain(PaymentRequestProblem.MissingReference)
            .And.Contain(PaymentRequestProblem.MissingCurrency)
            .And.Contain(PaymentRequestProblem.MissingIntendedUse)
            .And.Contain(PaymentRequestProblem.MissingReturnUrl);
    }

    [Fact]
    public void ARenewalWithABlankClaimPlanCadenceOrCurrency_IsRefused()
    {
        RenewalRequest().Problems(Now).Should().BeEmpty("negative control");

        (RenewalRequest() with { ClaimReference = " " }).Problems(Now).Should().Contain(PaymentRequestProblem.MissingReference);
        (RenewalRequest() with { PlanTier = "" }).Problems(Now).Should().Contain(PaymentRequestProblem.MissingPlanTerms);
        (RenewalRequest() with { Cadence = " " }).Problems(Now).Should().Contain(PaymentRequestProblem.MissingPlanTerms);
        (RenewalRequest() with { Currency = "" }).Problems(Now).Should().Contain(PaymentRequestProblem.MissingCurrency);
    }

    [Fact]
    public void ASetupFactStampedForAnotherSite_DoesNotSatisfy()
    {
        var request = SetupOnly();
        Succeeded(request).Satisfies(request, Session()).Should().BeTrue("negative control");

        var otherSite = Succeeded(request with { SiteId = "other-site" });
        otherSite.Mismatches(request, Session()).Should().Contain(nameof(PaymentMethodSetupFact.SiteId));
        otherSite.Satisfies(request, Session()).Should().BeFalse();
    }

    [Fact]
    public void ASucceededSetupThatNamesNoMethod_DoesNotSatisfy()
    {
        var request = SetupOnly();

        (Succeeded(request) with { PaymentMethodId = " " }).Satisfies(request, Session())
            .Should().BeFalse("a success with no saved method proves nothing reusable");
    }

    [Fact]
    public void ASubscriptionCreatedFromAnotherSetup_DoesNotSatisfy()
    {
        var request = RenewalRequest();
        Scheduled(request).Satisfies(request).Should().BeTrue("negative control");

        var fromOtherSetup = Scheduled(request with { SetupId = "seti_OTHER" });
        fromOtherSetup.Mismatches(request).Should().Contain(nameof(DeferredSubscriptionFact.SetupReference));
        fromOtherSetup.Satisfies(request).Should().BeFalse();
    }
}
