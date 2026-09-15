using System.Collections.Concurrent;
using System.Reactive.Linq;
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
            .ConfigureServices(services => services.AddSingleton<IEmailSender>(mail));

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
        stored.LocalizedTitle("de").Should().NotBe(stored.LocalizedTitle("en"));
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
