using System.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// The embedded payment-method form (MeshWeaver.Plugins#2064): <see cref="PaymentSetupFormControl"/>
/// round-trips through the hub's polymorphic JSON pipeline with its short <c>$type</c>, literals and
/// bindings alike — and, by construction, it has no member that could carry a provider's client secret.
/// </summary>
public class PaymentSetupFormControlSerializationTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutTypes();

    [Fact]
    public void ThePaymentSetupForm_RoundTrips_LiteralsAndBindings()
    {
        var options = GetClient().JsonSerializerOptions;
        var control = Controls.PaymentSetupForm(new JsonPointerReference("setupId"))
            .WithProvider("Stripe")
            .WithClientPublicKey("pk_test_51AbCdEf")
            .WithReturnUrl("https://portal.example.com/Store/CouponJourney/Review?link=0123")
            .WithNote(new JsonPointerReference("note"));
        var serialized = JsonSerializer.Serialize<UiControl>(control, options);
        using (var doc = JsonDocument.Parse(serialized))
            doc.RootElement.GetProperty("$type").GetString().Should().Be(nameof(PaymentSetupFormControl));
        var read = JsonSerializer.Deserialize<UiControl>(serialized, options).Should().BeOfType<PaymentSetupFormControl>().Subject;
        read.SetupId.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("setupId");
        read.Provider!.ToString().Should().Be("Stripe");
        read.ClientPublicKey!.ToString().Should().Be("pk_test_51AbCdEf");
        read.Note.Should().BeOfType<JsonPointerReference>();
    }

    /// <summary>
    /// 🚨 The control cannot carry a secret: no member is named for one, so a client secret can never
    /// land in a layout area's data, a stream snapshot or a node by being put on the control. The
    /// renderer obtains it in the buyer's own session instead.
    /// </summary>
    [Fact]
    public void ThePaymentSetupForm_HasNoMemberThatCouldCarryASecret()
    {
        typeof(PaymentSetupFormControl).GetProperties()
            .Select(p => p.Name)
            .Where(name => name.Contains("Secret", System.StringComparison.OrdinalIgnoreCase)
                           || name.Contains("Token", System.StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty();
    }
}
