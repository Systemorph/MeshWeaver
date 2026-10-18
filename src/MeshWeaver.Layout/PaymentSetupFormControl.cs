namespace MeshWeaver.Layout;

/// <summary>
/// The <b>embedded payment-method form</b> for one open payment setup — the provider's own in-page
/// controls (for Stripe: the Payment Element with the Express Checkout Element for Apple Pay and
/// Google Pay where the device and account are eligible), so a person saves a method on the page they
/// are on, without leaving it and without being charged.
///
/// <para>🚨 <b>The control carries no secret, by construction.</b> A provider's one-time client secret
/// (the value that lets a browser confirm a setup) is NOT a property of this record, so it can never
/// land in a layout area's data, a node, a layout-stream snapshot, a cache or a log line that
/// serializes controls. The control names only the setup (<see cref="SetupId"/>) and what is public
/// by nature (<see cref="ClientPublicKey"/>). The renderer, running server-side in the BUYER'S OWN
/// circuit, asks the site's payment provider for the secret with the circuit's signed-in identity
/// (<c>IPaymentMethodSetupProvider.RevealClientSecret(setupId, buyer)</c>), and the provider answers
/// only when that identity is the buyer the setup was opened for. The secret then crosses exactly one
/// boundary: from the server to that browser, straight into the provider's script.</para>
///
/// <para>The form never decides anything. Whether a method was saved is established afterwards from
/// the provider (a verified delivery or a server-side read), never from what the page reports.</para>
/// </summary>
/// <param name="SetupId">The provider's setup id the form completes — a literal or a data-binding pointer.</param>
public record PaymentSetupFormControl(object SetupId) : UiControl<PaymentSetupFormControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>The provider's display name (the platform's <c>IPaymentProvider.DisplayName</c>, e.g. <c>Stripe</c>), which selects the renderer — a literal or a binding.</summary>
    public object? Provider { get; init; }

    /// <summary>The provider's PUBLISHABLE key its in-page script loads with. Public by nature; never a secret key — a literal or a binding.</summary>
    public object? ClientPublicKey { get; init; }

    /// <summary>Where the provider returns the person after an off-page authentication step (3-D Secure), on this site — a literal or a binding.</summary>
    public object? ReturnUrl { get; init; }

    /// <summary>The page's own line under the form (for example "Nothing is charged today"); the platform's default when absent — a literal or a binding.</summary>
    public object? Note { get; init; }

    /// <summary>Names the provider whose renderer draws the form.</summary>
    /// <param name="provider">The provider's display name, or a binding.</param>
    public PaymentSetupFormControl WithProvider(object provider) => this with { Provider = provider };

    /// <summary>Sets the provider's publishable key.</summary>
    /// <param name="clientPublicKey">The publishable key, or a binding.</param>
    public PaymentSetupFormControl WithClientPublicKey(object clientPublicKey) => this with { ClientPublicKey = clientPublicKey };

    /// <summary>Sets the return address after an off-page authentication step.</summary>
    /// <param name="returnUrl">The address, or a binding.</param>
    public PaymentSetupFormControl WithReturnUrl(object returnUrl) => this with { ReturnUrl = returnUrl };

    /// <summary>Sets the line under the form.</summary>
    /// <param name="note">The text, or a binding.</param>
    public PaymentSetupFormControl WithNote(object note) => this with { Note = note };
}
