using System.Net;
using System.Text;
using System.Text.Json;
using MeshWeaver.Mesh.Security;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// The viewer's language, captured ONCE at the action's edge, and the catalog lookup every step-up
/// text renders with. Explicit on purpose: the actions continue downstream of store and pool
/// emissions, where the request's ambient access context — and with it the viewer's locale — is no
/// longer reliable.
/// </summary>
/// <param name="Locale">The resolved language tag.</param>
internal sealed record StepUpPageTexts(string Locale)
{
    /// <summary>The catalog text for <paramref name="key"/> in <see cref="Locale"/>, with positional arguments.</summary>
    /// <param name="key">The catalog key.</param>
    /// <param name="args">Positional arguments.</param>
    /// <returns>The text.</returns>
    public string L(string key, params object?[] args) => MeshWeaver.Messaging.LocalizationCatalog.Get(key, Locale, args);

    /// <summary>
    /// The culture of <see cref="Locale"/>, for dates and numbers on the page — resolved explicitly
    /// from the viewer's locale, never from the process culture. A tag .NET does not know formats
    /// invariant (the text itself still follows the catalog's own fallback).
    /// </summary>
    public System.Globalization.CultureInfo Culture
    {
        get
        {
            try { return System.Globalization.CultureInfo.GetCultureInfo(Locale); }
            catch (System.Globalization.CultureNotFoundException) { return System.Globalization.CultureInfo.InvariantCulture; }
        }
    }
}

/// <summary>
/// The few pages the passkey and TOTP rungs render themselves. They live OUTSIDE the Blazor shell by
/// necessity: a WebAuthn ceremony must run in the page that asked for it, and the flow is reached by
/// a full-page navigation (like the IdP round trip of the Entra rung). So they are deliberately
/// minimal — a heading, a sentence, one button or field — every word from the localization catalog,
/// and the only script is the WebAuthn call itself (<c>navigator.credentials.get/create</c>, no
/// framework). Nothing is decided here: every endpoint the script calls re-checks on the server.
/// </summary>
internal static class StepUpPages
{
    /// <summary>The page shell.</summary>
    internal static string Shell(StepUpPageTexts t, string body, string? script = null)
    {
        var html = new StringBuilder()
            .Append("<!doctype html><html lang=\"").Append(Enc(t.Locale)).Append("\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>")
            .Append(Enc(t.L("stepUp.title"))).Append("</title></head>")
            .Append("<body style=\"font-family:system-ui,sans-serif;max-width:40rem;margin:4rem auto;padding:0 1rem;line-height:1.5\">")
            .Append("<h1>").Append(Enc(t.L("stepUp.title"))).Append("</h1>")
            .Append(body);
        if (script is not null)
            html.Append("<script>").Append(script).Append("</script>");
        return html.Append("</body></html>").ToString();
    }

    /// <summary>A refusal or failure — one sentence and a way back.</summary>
    internal static string Message(StepUpPageTexts t, string message, string backUrl) =>
        Shell(t, "<p role=\"alert\">" + Enc(message) + "</p><p><a href=\"" + Enc(backUrl) + "\">" + Enc(t.L("stepUp.back")) + "</a></p>");

    /// <summary>No factor yet: enrol one before approving.</summary>
    internal static string EnrollNeeded(StepUpPageTexts t, string enrollUrl) =>
        Shell(t, P(t.L("stepUp.enrollNeeded")) + "<p><a href=\"" + Enc(enrollUrl) + "\">" + Enc(t.L("stepUp.enrollLink")) + "</a></p>");

    /// <summary>The passkey rung: one button that runs the assertion.</summary>
    internal static string Passkey(StepUpPageTexts t, string enrollUrl) =>
        Shell(t,
            P(t.L("stepUp.passkey.intro"))
            + "<p><button id=\"go\" type=\"button\">" + Enc(t.L("stepUp.passkey.button")) + "</button></p>"
            + "<p id=\"status\" role=\"status\"></p>"
            + "<p><a href=\"" + Enc(enrollUrl) + "\">" + Enc(t.L("stepUp.enroll.manage")) + "</a></p>",
            Script(t, """
                document.getElementById('go').addEventListener('click', async () => {
                  const status = document.getElementById('status');
                  if (!window.PublicKeyCredential) { status.textContent = T.unsupported; return; }
                  status.textContent = T.working;
                  try {
                    const options = await post('passkey/options', {});
                    if (options.error) { status.textContent = options.error; return; }
                    options.challenge = dec(options.challenge);
                    (options.allowCredentials || []).forEach(c => c.id = dec(c.id));
                    const cred = await navigator.credentials.get({ publicKey: options });
                    const answer = await post('passkey/verify', assertion(cred));
                    if (answer.redirect) { location.href = answer.redirect; return; }
                    status.textContent = answer.error || T.failed;
                  } catch (e) { status.textContent = T.failed; }
                });
                """));

    /// <summary>The TOTP rung: a code (or a recovery code) and a nudge towards a passkey.</summary>
    internal static string Totp(StepUpPageTexts t, string formAction, string enrollUrl) =>
        Shell(t,
            P(t.L("stepUp.totp.intro"))
            + "<form method=\"post\" action=\"" + Enc(formAction) + "\">"
            + "<p><label>" + Enc(t.L("stepUp.totp.code")) + " <input name=\"code\" autocomplete=\"one-time-code\" inputmode=\"text\" required autofocus></label> "
            + "<button type=\"submit\">" + Enc(t.L("stepUp.totp.submit")) + "</button></p></form>"
            + "<p><a href=\"" + Enc(enrollUrl) + "\">" + Enc(t.L("stepUp.nudgePasskey")) + "</a></p>");

    /// <summary>
    /// The enrolment page — the user's passkeys, "add a passkey", and the authenticator-app fallback
    /// offered ONLY when this device reports no passkey support and the account has no passkey.
    /// </summary>
    internal static string Enroll(StepUpPageTexts t, StepUpFactors? factors, bool mayEnrollTotp,
        string? receiptId, string? confirmFirstUrl, bool signInAgain, string returnUrl)
    {
        var body = new StringBuilder(P(t.L("stepUp.enroll.intro")));
        body.Append("<h2>").Append(Enc(t.L("stepUp.enroll.passkeys"))).Append("</h2>");
        var passkeys = factors?.Passkeys ?? [];
        if (passkeys.Count == 0)
            body.Append(P(t.L("stepUp.enroll.none")));
        else
        {
            body.Append("<ul>");
            // Formatted for the VIEWER's locale (never the process culture), oldest first.
            var culture = t.Culture;
            foreach (var p in passkeys.Values.OrderBy(p => p.CreatedAt))
                body.Append("<li>").Append(Enc(string.Format(culture,
                    t.L("stepUp.enroll.created"), p.CreatedAt.UtcDateTime.ToString("d", culture))))
                    .Append("</li>");
            body.Append("</ul>");
        }
        if (factors is { TotpConfirmedAt: not null })
            body.Append(P(t.L("stepUp.enroll.totpOn")));

        if (signInAgain)
            return Shell(t, body + P(t.L("stepUp.enroll.signInAgain")) + Back(t, returnUrl));
        if (confirmFirstUrl is not null)
            return Shell(t, body + P(t.L("stepUp.enroll.confirmFirst"))
                + "<p><a href=\"" + Enc(confirmFirstUrl) + "\">" + Enc(t.L("stepUp.confirm")) + "</a></p>" + Back(t, returnUrl));

        body.Append("<p><button id=\"add\" type=\"button\">").Append(Enc(t.L("stepUp.enroll.addPasskey"))).Append("</button></p>");
        if (mayEnrollTotp)
            body.Append("<div id=\"totp\" hidden><h2>").Append(Enc(t.L("stepUp.enroll.totp"))).Append("</h2>")
                .Append("<p><button id=\"totpStart\" type=\"button\">").Append(Enc(t.L("stepUp.enroll.totpStart"))).Append("</button></p>")
                .Append("<div id=\"totpSetup\" hidden>").Append(P(t.L("stepUp.enroll.totpScan")))
                .Append("<div id=\"qr\" style=\"max-width:16rem\"></div><p><code id=\"secret\"></code></p>")
                .Append("<p><label>").Append(Enc(t.L("stepUp.totp.code"))).Append(" <input id=\"totpCode\" autocomplete=\"one-time-code\" inputmode=\"numeric\"></label> ")
                .Append("<button id=\"totpConfirm\" type=\"button\">").Append(Enc(t.L("stepUp.totp.submit"))).Append("</button></p></div>")
                .Append("<div id=\"recovery\" hidden>").Append(P(t.L("stepUp.enroll.recovery"))).Append("<pre id=\"codes\"></pre></div></div>");
        body.Append("<p id=\"status\" role=\"status\"></p>").Append(Back(t, returnUrl));

        var receipt = JsonSerializer.Serialize(receiptId);
        return Shell(t, body.ToString(), Script(t, "const RECEIPT = " + receipt + ";\n" + """
            const status = document.getElementById('status');
            const totp = document.getElementById('totp');
            (async () => {
              // TOTP is offered only where this device can do no passkey at all.
              const webauthn = !!window.PublicKeyCredential;
              const platform = webauthn && await PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable().catch(() => false);
              if (totp && !platform) totp.hidden = false;
              if (!webauthn) document.getElementById('add').disabled = true;
            })();
            document.getElementById('add').addEventListener('click', async () => {
              status.textContent = T.working;
              try {
                const options = await post('passkey/register/options', { receipt: RECEIPT });
                if (options.error) { status.textContent = options.error; return; }
                options.challenge = dec(options.challenge);
                options.user.id = dec(options.user.id);
                (options.excludeCredentials || []).forEach(c => c.id = dec(c.id));
                const cred = await navigator.credentials.create({ publicKey: options });
                const answer = await post('passkey/register', { receipt: RECEIPT, credential: JSON.stringify(attestation(cred)) });
                if (answer.ok) { status.textContent = T.done; location.reload(); return; }
                status.textContent = answer.error || T.failed;
              } catch (e) { status.textContent = T.failed; }
            });
            const start = document.getElementById('totpStart');
            if (start) start.addEventListener('click', async () => {
              const setup = await post('totp/enroll/start', { receipt: RECEIPT });
              if (setup.error) { status.textContent = setup.error; return; }
              document.getElementById('qr').innerHTML = setup.svg;
              document.getElementById('secret').textContent = setup.secret;
              document.getElementById('totpSetup').hidden = false;
            });
            const confirm = document.getElementById('totpConfirm');
            if (confirm) confirm.addEventListener('click', async () => {
              const answer = await post('totp/enroll/confirm', { receipt: RECEIPT, code: document.getElementById('totpCode').value });
              if (answer.error) { status.textContent = answer.error; return; }
              document.getElementById('totpSetup').hidden = true;
              document.getElementById('codes').textContent = answer.recoveryCodes.join('\n');
              document.getElementById('recovery').hidden = false;
            });
            """));
    }

    /// <summary>
    /// The shared script prelude: the localized status words, base64url helpers, a JSON POST to a
    /// sibling endpoint, and the two WebAuthn response serializers.
    /// </summary>
    private static string Script(StepUpPageTexts t, string body) =>
        "const T = " + JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["working"] = t.L("stepUp.working"),
            ["failed"] = t.L("stepUp.failed.generic"),
            ["unsupported"] = t.L("stepUp.passkey.unsupported"),
            ["done"] = t.L("stepUp.enroll.done"),
        }) + ";\n" + """
            const enc = b => btoa(String.fromCharCode(...new Uint8Array(b))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
            const dec = s => Uint8Array.from(atob(s.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - s.length % 4) % 4)), c => c.charCodeAt(0));
            const post = async (path, body) => {
              const r = await fetch('/auth/step-up/' + path, { method: 'POST', credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
              return r.json();
            };
            const assertion = c => ({ id: c.id, rawId: enc(c.rawId), type: c.type,
              response: { authenticatorData: enc(c.response.authenticatorData), clientDataJSON: enc(c.response.clientDataJSON),
                signature: enc(c.response.signature), userHandle: c.response.userHandle ? enc(c.response.userHandle) : null },
              clientExtensionResults: c.getClientExtensionResults() });
            const attestation = c => ({ id: c.id, rawId: enc(c.rawId), type: c.type,
              response: { attestationObject: enc(c.response.attestationObject), clientDataJSON: enc(c.response.clientDataJSON),
                transports: c.response.getTransports ? c.response.getTransports() : [] },
              clientExtensionResults: c.getClientExtensionResults() });

            """ + body;

    private static string Back(StepUpPageTexts t, string url) =>
        "<p><a href=\"" + Enc(url) + "\">" + Enc(t.L("stepUp.back")) + "</a></p>";

    private static string P(string text) => "<p>" + Enc(text) + "</p>";

    private static string Enc(string? text) => WebUtility.HtmlEncode(text ?? "");
}
