using MeshWeaver.Data;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>Which credential a GitHub read authenticates with — the decision
/// <see cref="GitHubSyncService"/> takes for every import, update and branch check.</summary>
public enum GitHubReadIdentityKind
{
    /// <summary>The acting person's own connected GitHub credential (<c>{user}/_Provider/GitHub</c>).</summary>
    PersonConnected,

    /// <summary>A token the repository's configured source declared by configuration key
    /// (<c>PluginCatalog:Sources:N:TokenKey</c>) — typically a personal access token.</summary>
    SourceToken,

    /// <summary>The instance's GitHub App, through the installation the source named
    /// (<c>PluginCatalog:Sources:N:InstallationOwner</c>).</summary>
    SourceInstallation,

    /// <summary>The instance's own GitHub App installation (<c>GitHub:App:*</c>).</summary>
    AppInstallation,

    /// <summary>No identity is available; the read is refused.</summary>
    None,
}

/// <summary>
/// The identity a GitHub read of one repository authenticates as, by NAME only — the login, the
/// configuration key, the installation; never a token. Produced by
/// <see cref="GitHubSyncService.ReadIdentityFor"/>, which takes the SAME decision the token
/// resolution takes, so what an activity says it used is what it used.
///
/// <para>Why it is written on the activity: an operator asked to prove that a sync runs on the App
/// and not on a personal access token had nothing to read — the import log said "Imported" and
/// nothing about who read the repository (MeshWeaver: "the sync activity names its GitHub
/// identity").</para>
/// </summary>
/// <param name="Kind">Which credential.</param>
/// <param name="Login">The person's GitHub login, for <see cref="GitHubReadIdentityKind.PersonConnected"/>.</param>
/// <param name="TokenKey">The configuration key the token is read from, for
/// <see cref="GitHubReadIdentityKind.SourceToken"/>.</param>
/// <param name="InstallationOwner">The account whose installation mints the token.</param>
/// <param name="InstallationId">The configured installation id of the instance's own App, when set.</param>
public sealed record GitHubReadIdentity(
    GitHubReadIdentityKind Kind,
    string? Login = null,
    string? TokenKey = null,
    string? InstallationOwner = null,
    long? InstallationId = null)
{
    /// <summary>The installation as a reader names it: <c>162001736 (Systemorph)</c>, or whichever half is known.</summary>
    public string InstallationLabel =>
        (InstallationId, InstallationOwner) switch
        {
            ({ } id, { Length: > 0 } owner) => $"{id} ({owner})",
            ({ } id, _) => id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (null, { Length: > 0 } owner) => $"on '{owner}'",
            _ => "discovered at mint",
        };

    /// <summary>The activity line naming this identity, keyed for the catalog
    /// (<c>activity.gitsync.identity.*</c>). Pure.</summary>
    public LogMessage ToLogMessage() => Kind switch
    {
        GitHubReadIdentityKind.PersonConnected => new LogMessage(
                $"GitHub identity: the connected GitHub credential of @{Login ?? "?"}.",
                LogLevel.Information)
            .WithKey("activity.gitsync.identity.person", ("login", Login ?? "?")),
        GitHubReadIdentityKind.SourceToken => new LogMessage(
                $"GitHub identity: the token the source declared under configuration key '{TokenKey}'.",
                LogLevel.Information)
            .WithKey("activity.gitsync.identity.sourceToken", ("key", TokenKey ?? "")),
        GitHubReadIdentityKind.SourceInstallation => new LogMessage(
                $"GitHub identity: the GitHub App installation on '{InstallationOwner}' the source named.",
                LogLevel.Information)
            .WithKey("activity.gitsync.identity.sourceInstallation", ("owner", InstallationOwner ?? "")),
        GitHubReadIdentityKind.AppInstallation => new LogMessage(
                $"GitHub identity: this instance's GitHub App, installation {InstallationLabel}.",
                LogLevel.Information)
            .WithKey("activity.gitsync.identity.app", ("installation", InstallationLabel)),
        _ => new LogMessage(
                "GitHub identity: none — no connected credential, no declared source credential, no GitHub App.",
                LogLevel.Warning)
            .WithKey("activity.gitsync.identity.none"),
    };
}
