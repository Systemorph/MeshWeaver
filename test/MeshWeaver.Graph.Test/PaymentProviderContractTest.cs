#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Payments;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the ADDITIONS to the payment provider contract: the hosted billing portal and the
/// failed-charge delivery.
///
/// <para>🚨 The billing portal is a DEFAULT-IMPLEMENTED member on purpose — a provider written
/// before it existed (every satellite's, and any fake a dependent's tests implement) keeps
/// compiling and simply offers no portal. <see cref="AProviderThatDoesNotOptIn_OffersNoPortal_AndRefusesLoudly"/>
/// implements ONLY the pre-existing members, so if a future edit drops a default this file stops
/// compiling, which is the implementer-break the change must never cause.</para>
/// </summary>
public class PaymentProviderContractTest
{
    /// <summary>A provider written against the contract as it was BEFORE the portal existed.</summary>
    private sealed class OlderProvider : IPaymentProvider
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
    public void AProviderThatDoesNotOptIn_OffersNoPortal_AndRefusesLoudly()
    {
        IPaymentProvider provider = new OlderProvider();
        provider.OffersBillingPortal.Should().BeFalse(
            "a provider that never implemented the portal must not be offered as having one");

        // Observable.Throw signals synchronously on Subscribe — no scheduler, no wait.
        Exception? error = null;
        var emitted = false;
        using (provider.OpenBillingPortal(new PaymentBillingPortalRequest
               {
                   CustomerId = "cus_1",
                   ReturnUrl = "https://portal.example/Store/Plans",
               })
               .Subscribe(_ => emitted = true, e => error = e))
        {
        }

        emitted.Should().BeFalse("a provider with no portal must never answer with a URL");
        error.Should().BeOfType<NotSupportedException>(
            "asking anyway is an error, never a silence a caller could read as 'still opening'");
        error!.Message.Should().Contain("Older", "the refusal names the provider");
    }

    /// <summary>
    /// The governed endpoint members (Plugins#1881) are default-implemented: a provider written
    /// before them keeps compiling, offers no endpoint task, and both calls ANSWER NotSupported —
    /// never a throw, never a silence.
    /// </summary>
    [Fact]
    public void AProviderThatDoesNotOptIn_ManagesNoEndpoint_AndAnswersNotSupported()
    {
        IPaymentProvider provider = new OlderProvider();
        provider.ManagesDeliveryEndpoints.Should().BeFalse();
        var request = new PaymentEndpointRequest
        {
            Url = "https://portal.example/api/hooks/Store/Payments",
            Owner = "https://portal.example",
            RequestPath = "Admin/Maintenance/ensure",
            Execute = true,
            ConfirmedMode = "test",
        };

        PaymentEndpointChange? ensured = null, removed = null;
        using (provider.EnsureDeliveryEndpoint(request).Subscribe(c => ensured = c)) { }
        using (provider.RemoveDeliveryEndpoint(request).Subscribe(c => removed = c)) { }

        ensured!.Outcome.Should().Be(PaymentEndpointOutcome.NotSupported);
        ensured.Refusal!.English.Should().Contain("Older", "the refusal names the provider");
        ensured.Refusal.Key.Should().Be(IPaymentProvider.EnsureNotSupportedKey, "the refusal is keyed for the viewer's language");
        ensured.DisclosedSecret.Should().BeNull();
        removed!.Outcome.Should().Be(PaymentEndpointOutcome.NotSupported);
        removed.Refusal!.Key.Should().Be(IPaymentProvider.RemoveNotSupportedKey,
            "the two defaults are two different refusals, each with its own remedy");
    }

    /// <summary>A printed change never carries the signing secret; the negative control shows the field is set.</summary>
    [Fact]
    public void APrintedChange_MasksTheDisclosedSecret()
    {
        var change = new PaymentEndpointChange
        {
            Outcome = PaymentEndpointOutcome.Created,
            EndpointId = "we_1",
            DisclosedSecret = "whsec_abc123",
        };
        change.DisclosedSecret.Should().Be("whsec_abc123", "negative control: the secret IS on the record");
        change.ToString().Should().NotContain("whsec_abc123").And.Contain("(redacted)").And.Contain("we_1");

        // Serialized, the secret is ABSENT — persisted, posted or logged JSON carries none.
        var json = System.Text.Json.JsonSerializer.Serialize(change);
        json.Should().NotContain("whsec_abc123").And.NotContain(nameof(PaymentEndpointChange.DisclosedSecret))
            .And.Contain("we_1", "negative control: the rest of the change IS serialized");
    }

    [Fact]
    public void APortalRequest_NamesItsSubscriber_ByCustomerOrBySubscription()
    {
        new PaymentBillingPortalRequest { CustomerId = "cus_1", ReturnUrl = "u" }
            .NamesSubscriber.Should().BeTrue();
        new PaymentBillingPortalRequest { SubscriptionId = "sub_1", ReturnUrl = "u" }
            .NamesSubscriber.Should().BeTrue("a plan sold before the customer was recorded still gets a portal");
        new PaymentBillingPortalRequest { CustomerId = " ", ReturnUrl = "u" }
            .NamesSubscriber.Should().BeFalse();
    }

    [Fact]
    public void AFailedCharge_IsWorkThePortalActsOn_AndNeitherARenewalNorAnEnding()
    {
        var failed = new PaymentSubscriptionEvent(
            PaymentSubscriptionChange.PaymentFailed, "sub_1",
            ImmutableDictionary<string, string>.Empty
                .Add(PaymentMetadata.Buyer, "alice")
                .Add(PaymentMetadata.PlanTier, "personal"))
        {
            CustomerId = "cus_1",
            InvoiceId = "in_1",
        };

        failed.FailsPayment.Should().BeTrue();
        failed.Renews.Should().BeFalse("a failed charge pays for nothing");
        failed.Ends.Should().BeFalse("the processor retries — the plan is past-due, not over");
        failed.IsActionable.Should().BeTrue();
        // The retention DECISION is the commerce caller's (the Store's inbox watcher); what the
        // contract owes it is that an UNVERIFIABLE failed-charge delivery still NamesWork — the one
        // predicate that decision turns on (retain as evidence vs discard as junk).
        var unverifiable = new PaymentDelivery { CanAuthenticate = true, Authentic = false, Subscription = failed };
        unverifiable.Authentic.Should().BeFalse();
        unverifiable.NamesWork.Should().BeTrue(
            "an unverifiable failed-charge delivery must be RETAINED as evidence, like a renewal");
        new PaymentDelivery
            {
                CanAuthenticate = true,
                Authentic = false,
                Subscription = failed with { Metadata = ImmutableDictionary<string, string>.Empty, Change = PaymentSubscriptionChange.None },
            }
            .NamesWork.Should().BeFalse("…while one that names nothing is junk and may be discarded");
    }

    private static PaymentSubscriptionEvent Updated(string? renewal) =>
        new(PaymentSubscriptionChange.None, "sub_1",
            ImmutableDictionary<string, string>.Empty
                .Add(PaymentMetadata.Buyer, "alice")
                .Add(PaymentMetadata.PlanTier, "personal"))
        {
            Renewal = renewal,
            OccurredAt = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero),
        };

    /// <summary>
    /// Plugins#3548: a cancellation requested on the processor's own page is reported when it is
    /// REQUESTED, beside an unchanged period — retained as work, and neither a renewal nor an ending.
    /// </summary>
    [Fact]
    public void ARequestedCancellation_IsWork_AndChangesNoPeriod()
    {
        var cancels = Updated(PaymentSubscriptionRenewal.CancelsAtPeriodEnd);
        cancels.CancelsAtPeriodEnd.Should().BeTrue();
        cancels.ResumesRenewal.Should().BeFalse();
        cancels.ChangesRenewal.Should().BeTrue();
        cancels.Renews.Should().BeFalse("a requested cancellation pays for nothing");
        cancels.Ends.Should().BeFalse("the subscriber paid through the period — the plan ends at its end, not now");
        cancels.IsActionable.Should().BeTrue();
        new PaymentDelivery { CanAuthenticate = true, Authentic = false, Subscription = cancels }
            .NamesWork.Should().BeTrue("an unverifiable cancellation request is retained as evidence");

        var resumes = Updated(PaymentSubscriptionRenewal.Renews);
        resumes.ResumesRenewal.Should().BeTrue();
        resumes.CancelsAtPeriodEnd.Should().BeFalse();
        resumes.IsActionable.Should().BeTrue("withdrawing the request must clear what the plan says");
    }

    /// <summary>
    /// Negative control: an update that reports NO change to the renewal state is not work, and an
    /// unknown provider word is retained but is never read as either known word.
    /// </summary>
    [Fact]
    public void AnUpdateWithoutARenewalChange_IsNotWork_AndAnUnknownWordIsNeitherKnownWord()
    {
        var unrelated = Updated(null);
        unrelated.ChangesRenewal.Should().BeFalse();
        unrelated.IsActionable.Should().BeFalse("an update that changes neither period nor renewal is nothing to do");
        new PaymentDelivery { CanAuthenticate = true, Authentic = true, Subscription = unrelated }
            .NamesWork.Should().BeFalse();
        Updated(" ").ChangesRenewal.Should().BeFalse("a blank word reports nothing");

        var unknown = Updated("PausesCollection");
        unknown.ChangesRenewal.Should().BeTrue("an open vocabulary: a provider's own word is retained so the caller can name it");
        unknown.CancelsAtPeriodEnd.Should().BeFalse();
        unknown.ResumesRenewal.Should().BeFalse("an unknown word is never read as either known word");
    }
}
