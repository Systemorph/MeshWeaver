using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// The invitation email — subject and HTML body — rendered in the language stored on the invitation
/// (<see cref="Invitation.Locale"/>, #7482). The invitee has no account yet, so there is no profile to
/// read a language from: the inviting host picked it (defaulting to the host's own), and it was
/// stamped on the invitation when it was created. Every word comes from the string catalog
/// (<c>notification.email.invitation.*</c>), resolved with that tag through
/// <see cref="LocalizationCatalog"/>, never the process culture; the standing footer is rendered in
/// the same language (<see cref="EmailTemplate.Build(string, IReadOnlyList{string}, string, string, string, string)"/>).
///
/// <para>When the invitation targets a Space or group (<see cref="Invitation.SpacePath"/> set) it
/// addresses it by <c>spaceName</c> (falling back to the path) and links straight to it
/// (<c>{baseUrl}/{SpacePath}</c>); otherwise it renders the deployment-wide invitation. The host's
/// personal <see cref="Invitation.Message"/>, when present, is its own paragraph, HTML-encoded by the
/// template; the admin-facing <see cref="Invitation.Note"/> never reaches the email. Pure and static,
/// so the sender (MeshWeaver.Plugins' <c>InvitationEmailSender</c>) and tests render the same bytes.</para>
/// </summary>
public static class InvitationEmail
{
    /// <summary>
    /// Renders the invitation email in <paramref name="invitation"/>'s <see cref="Invitation.Locale"/>
    /// (English when it carries none, i.e. an invitation written before the field existed).
    /// </summary>
    /// <param name="invitation">The invitation being emailed.</param>
    /// <param name="spaceName">The display name of <see cref="Invitation.SpacePath"/>, or null for the path.</param>
    /// <param name="baseUrl">The portal's public base URL, or null for an email without a link.</param>
    /// <returns>The subject line and the HTML body.</returns>
    public static (string Subject, string Html) Build(Invitation invitation, string? spaceName, string? baseUrl)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        var locale = Locales.Resolve(invitation.Locale);
        string T(string key) => LocalizationCatalog.Get(key, locale);
        // notification.* keys carry NAMED placeholders ({space}) by catalog rule.
        string TSpace(string key, string space)
            => LocalizationCatalog.GetNamed(key, locale, new Dictionary<string, object> { ["space"] = space });
        var hasBase = !string.IsNullOrWhiteSpace(baseUrl);
        var signInHint = T("notification.email.invitation.signInHint");

        if (string.IsNullOrWhiteSpace(invitation.SpacePath))
        {
            var subject = T("notification.email.invitation.genericSubject");
            return (subject, EmailTemplate.Build(
                heading: subject,
                paragraphs: Paragraphs(T("notification.email.invitation.genericBody"), invitation.Message),
                ctaLabel: hasBase ? T("notification.email.invitation.openMemex") : null,
                ctaUrl: hasBase ? baseUrl : null,
                footerNote: signInHint,
                locale: locale));
        }

        var spacePath = invitation.SpacePath!.Trim();
        var display = string.IsNullOrWhiteSpace(spaceName) ? spacePath : spaceName!.Trim();
        var spaceUrl = hasBase ? $"{baseUrl!.TrimEnd('/')}/{spacePath.TrimStart('/')}" : null;
        var spaceSubject = TSpace("notification.email.invitation.spaceSubject", display);
        return (spaceSubject, EmailTemplate.Build(
            heading: spaceSubject,
            paragraphs: Paragraphs(TSpace("notification.email.invitation.spaceBody", display), invitation.Message),
            ctaLabel: spaceUrl is null ? null : TSpace("notification.email.invitation.openSpace", display),
            ctaUrl: spaceUrl,
            footerNote: signInHint,
            locale: locale));
    }

    private static IReadOnlyList<string> Paragraphs(string body, string? message)
        => string.IsNullOrWhiteSpace(message) ? [body] : [body, message.Trim()];
}
