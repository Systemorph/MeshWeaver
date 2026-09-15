using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using Microsoft.Extensions.Configuration;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The preference-aware entry point, <see cref="NotificationService.DispatchLocalizable"/>, which is
/// what every migrated emitter actually calls — covered here because its TWO channels resolve the
/// language in OPPOSITE ways and each way is only correct for its own channel
/// (Systemorph/MeshWeaver#4373):
/// <list type="bullet">
///   <item><b>In-app</b> — the bell row is durable and read by several people, so the KEY and its
///     arguments are PERSISTED and the bell resolves per viewer. A regression that flattened the
///     key to its English fallback here would leave
///     <see cref="NotificationLocalizationTest"/> entirely green, because that file drives
///     <c>CreateLocalizableNotification</c> directly and never crosses the dispatch.</item>
///   <item><b>Email</b> — exactly ONE reader, known at send time, and no chance to re-render, so it
///     is resolved HERE against that person's own <c>User.Locale</c>. This is the only surface in
///     the change whose language is decided at write time, and the only way to see it is to read
///     the envelope that left the process.</item>
/// </list>
///
/// <para>The email sender is a real <see cref="IEmailSender"/> implementation registered in the mesh
/// — an outbound IO port recorded, not a mocked core interface — following
/// <c>EmailCopiedRecipientsTest</c>'s <c>EnvelopeRecordingSender</c>.</para>
/// </summary>
public class NotificationDispatchLocalizationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly RecordingEmailSender mail = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IEmailSender>(mail);

                // 🚨 The CTA button is emitted only when a base URL resolves, so a mesh without one
                // sends an email with NO button — and an assertion about the CTA label would then
                // pass by asserting nothing was there.
                //
                // 🚨 And it must LAYER onto the host's configuration, never replace it. A bare
                // AddSingleton<IConfiguration> wins the resolve and takes every other key with it;
                // HeldSourceSaysItIsHeldTest measured that as a credential save refusing with
                // "no master key is configured (Ai:KeyProtection:MasterKey)" — a failure about a key
                // the test never mentioned.
                var configured = services.LastOrDefault(d => d.ServiceType == typeof(IConfiguration));
                if (configured is not null)
                    services.Remove(configured);
                return services.AddSingleton<IConfiguration>(sp =>
                {
                    var layered = new ConfigurationBuilder();
                    if (Materialise(configured, sp) is { } host)
                        layered.AddConfiguration(host);
                    return layered
                        .AddInMemoryCollection(
                            ImmutableDictionary<string, string?>.Empty.Add("Portal:BaseUrl", BaseUrl))
                        .Build();
                });
            });

    /// <summary>The host's own <c>IConfiguration</c>, from whichever registration shape it used, or
    /// null when the host registered none. Never silently empty on an unrecognised shape: a test
    /// whose mesh lost every configuration key fails somewhere else entirely.</summary>
    private static IConfiguration? Materialise(ServiceDescriptor? descriptor, IServiceProvider sp)
        => descriptor is null ? null
            : descriptor.ImplementationFactory is { } factory ? (IConfiguration)factory(sp)
            : descriptor.ImplementationInstance is IConfiguration instance ? instance
            : throw new InvalidOperationException(
                "The IConfiguration registration is neither a factory nor an instance, so this test "
                + "cannot layer onto it — and silently dropping it would lose every other key.");

    private const string BaseUrl = "https://portal.test";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private System.Text.Json.JsonSerializerOptions Json => Mesh.JsonSerializerOptions;

    private async Task CreateUser(string id, string email, string? locale)
    {
        using (Access.ImpersonateAsSystem())
            await MeshService.CreateNode(new MeshNode(id)
            {
                NodeType = "User",
                Name = id,
                Content = new User { Email = email, FullName = id, Locale = locale },
            }).Should().Emit();
    }

    // The access-granted sentence, which ships in both catalogs and is what AccessGrantNotifier now
    // sends — so this measures the LIVE catalog rather than a fixture of its own.
    private static LocalizableText Title(string name) => LocalizableText.Keyed(
        $"You've been given access to {name}", "notification.accessGranted.title", ("name", name));

    /// <summary>
    /// 🚨 The dispatch must hand the KEY to the bell write, not the flattened English. Nothing else
    /// in the suite crosses this seam, and a regression here is silent: the row still says the right
    /// thing — in English, for every viewer, which is the bug.
    /// </summary>
    [Fact]
    public async Task TheInAppLeg_PersistsTheKeyAndItsArguments()
    {
        const string recipient = "loc_inapp";
        await CreateUser(recipient, "inapp@acme.com", locale: "de");

        await NotificationService.DispatchLocalizable(
                Mesh,
                recipient: recipient,
                mainNodePath: recipient,
                title: Title("TeamSpace"),
                message: LocalizableText.Keyed(
                    "You now have Editor access to \"TeamSpace\".",
                    "notification.accessGranted.body", ("role", "Editor"), ("name", "TeamSpace")),
                type: NotificationType.AccessGranted,
                targetNodePath: "TeamSpace",
                createdBy: "admin")
            .Timeout(TestTimeouts.Convergence).Await();

        var stored = await Mesh.GetWorkspace()
            .GetQuery($"notif|{recipient}",
                $"path:{recipient}/_Notification scope:children nodeType:Notification")
            .Select(nodes => (nodes ?? [])
                .Select(n => n.ContentAs<Notification>(Json))
                .FirstOrDefault(n => n is { NotificationType: NotificationType.AccessGranted }))
            .Where(n => n is not null)
            .FirstAsync().Timeout(TestTimeouts.Convergence);

        stored!.TitleKey.Should().Be("notification.accessGranted.title",
            "the dispatch must carry the key through to the bell write — flattening it here would "
            + "leave the direct-write tests green while every viewer read English");
        stored.MessageKey.Should().Be("notification.accessGranted.body");
        stored.TitleArgs.Should().ContainKey("name");
        // 🚨 BOTH message argument names, and the SUBSTITUTED values. A dispatch that carried the
        // keys but dropped the arguments would satisfy every key assertion above and render
        // "Sie haben jetzt {role}-Zugriff auf „{name}“." to the reader — GetNamed deliberately keeps
        // an unbound name VISIBLE, so the damage is a literal placeholder, not a blank.
        // BY NAME, not "both values appear somewhere": a dispatch that swapped them
        // (role = TeamSpace, name = Editor) renders a wrong sentence out of the right words, and a
        // contains-both assertion would call that a pass. Values come back from storage as
        // JsonElement, so compare their rendered text rather than casting (the silent-null trap).
        stored.MessageArgs!["role"].ToString().Should().Be("Editor");
        stored.MessageArgs["name"].ToString().Should().Be("TeamSpace");
        stored.TitleArgs!["name"].ToString().Should().Be("TeamSpace");
        stored.LocalizedMessage("de").Should().Contain("Editor").And.Contain("TeamSpace");
        stored.LocalizedMessage("de").Should().NotContain("{",
            "an unbound named argument survives as a literal {name} in the rendered sentence");
        stored.LocalizedTitle("de").Should().NotBe(stored.LocalizedTitle("en"));
        stored.LocalizedMessage("de").Should().NotBe(stored.LocalizedMessage("en"));
        stored.Title.Should().Be(stored.LocalizedTitle("en"),
            "the stored English is still the fallback and still what the key renders in English");
    }

    /// <summary>
    /// 🚨 The email leg, which is the ONE place a notification's language is decided while writing —
    /// legitimately, because a message has exactly one reader and cannot be re-rendered. It must be
    /// decided from the RECIPIENT's profile, not from the server's default: subject, body, the CTA
    /// button and the footer note.
    /// </summary>
    [Fact]
    public async Task TheEmailLeg_IsWrittenInTheRecipientsOwnLanguage()
    {
        const string recipient = "loc_email_de";
        await CreateUser(recipient, "de@acme.com", locale: "de");

        await NotificationService.DispatchLocalizable(
                Mesh,
                recipient: recipient,
                mainNodePath: recipient,
                title: Title("Quarterly Report"),
                message: LocalizableText.Keyed(
                    "You now have Editor access to \"Quarterly Report\".",
                    "notification.accessGranted.body", ("role", "Editor"), ("name", "Quarterly Report")),
                type: NotificationType.AccessGranted,
                targetNodePath: "Quarterly Report",
                createdBy: "admin",
                emailCtaLabel: LocalizableText.Keyed(
                    "Open Quarterly Report", "notification.accessGranted.emailCta",
                    ("name", "Quarterly Report")),
                emailFooterNote: LocalizableText.Keyed(
                    "New to Memex? Sign in with this email address to open it.",
                    "notification.accessGranted.emailFooter"))
            .Timeout(TestTimeouts.Convergence).Await();

        var sent = await mail.Sent.FirstAsync().Timeout(TestTimeouts.Convergence);

        sent.To.Should().Be("de@acme.com");
        sent.Subject.Should().Be(
            LocalizationCatalog.GetNamed("notification.accessGranted.title", "de",
                System.Collections.Immutable.ImmutableDictionary<string, object>.Empty
                    .Add("name", "Quarterly Report")),
            "the subject must be the recipient's language, resolved off THEIR User.Locale");
        sent.Subject.Should().NotBe("You've been given access to Quarterly Report",
            "resolving against the server default is the defect #4373 is about");
        // 🚨 The BODY and the CTA LABEL, not just the subject. Each is resolved by its own call
        // (`message.Localize`, `Rendered(ctaLabel, …)`), so a regression flattening either one to
        // English would leave a subject-only assertion green — a half-German mail.
        sent.Body.Should().Contain("Sie haben jetzt Editor-Zugriff",
            "the body is resolved by its own Localize call and needs its own assertion");
        sent.Body.Should().NotContain("You now have Editor access");
        // The template HTML-encodes, so assert the encoded form of the German CTA ("Öffnen" → "&#214;").
        sent.Body.Should().Contain("Quarterly Report &#246;ffnen",
            "the CTA label is resolved by a third call and needs a third assertion");
        sent.Body.Should().NotContain(">Open Quarterly Report<");
        // 🚨 The HREF, not the URL text. EmailTemplate also prints the raw URL in a <p> beneath the
        // button, so matching the bare URL would still pass on a regression that dropped the <a>
        // and kept the fallback line — a non-vacuity control that is itself vacuous.
        sent.Body.Should().Contain($"<a href=\"{BaseUrl}/Quarterly Report\"",
            "the control on the assertion above: a button that was never rendered would make the "
            + "German-label check pass by checking nothing — the CTA is emitted only when a base "
            + "URL resolves, which is why this mesh configures one");
        // The template HTML-encodes every caller string, so assert on a distinctive un-encoded
        // fragment rather than on the whole sentence ("öffnen" arrives as "&#246;ffnen").
        sent.Body.Should().Contain("Neu bei Memex?",
            "the first-contact footer follows the recipient too — a half-translated mail is the "
            + "same bug, smaller");
        sent.Body.Should().NotContain("New to Memex?");
        sent.Body.Should().Contain(
            LocalizationCatalog.Get("notification.email.standingFooter", "de"),
            "the template's OWN standing footer was a bare English literal compiled into the view, "
            + "which is the unowned case — not a third category");
        sent.Body.Should().NotContain("You received this because someone shared content");
    }

    /// <summary>
    /// The control that makes the assertion above mean something: the SAME dispatch, to a recipient
    /// whose profile says English, must produce the English envelope. Without this the German
    /// assertion would also pass on an implementation that always sent German.
    /// </summary>
    [Fact]
    public async Task TheEmailLeg_FollowsTheRecipient_NotTheServer()
    {
        const string recipient = "loc_email_en";
        await CreateUser(recipient, "en@acme.com", locale: "en");

        await NotificationService.DispatchLocalizable(
                Mesh,
                recipient: recipient,
                mainNodePath: recipient,
                title: Title("Quarterly Report"),
                message: LocalizableText.Verbatim("body"),
                type: NotificationType.AccessGranted,
                targetNodePath: "Quarterly Report",
                createdBy: "admin")
            .Timeout(TestTimeouts.Convergence).Await();

        var sent = await mail.Sent.FirstAsync().Timeout(TestTimeouts.Convergence);

        sent.To.Should().Be("en@acme.com");
        sent.Subject.Should().Be("You've been given access to Quarterly Report");
    }

    /// <summary>Records the envelope of every send — a real sender, not a mock.</summary>
    private sealed class RecordingEmailSender : IEmailSender
    {
        private readonly ConcurrentQueue<(string To, string Subject, string Body)> sent = new();
        private readonly System.Reactive.Subjects.ReplaySubject<(string To, string Subject, string Body)>
            sends = new();

        /// <summary>Every envelope that left, replayed — so a test may subscribe after the send.</summary>
        public IObservable<(string To, string Subject, string Body)> Sent => sends;

        public IObservable<bool> SendEmail(string toAddress, string subject, string htmlBody)
        {
            var envelope = (toAddress, subject, htmlBody);
            sent.Enqueue(envelope);
            sends.OnNext(envelope);
            return Observable.Return(true);
        }

        public IObservable<bool> SendEmail(
            string toAddress, string subject, string htmlBody,
            IReadOnlyCollection<EmailAttachment> attachments)
            => SendEmail(toAddress, subject, htmlBody);
    }
}
