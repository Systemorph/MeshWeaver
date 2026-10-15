using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MeshWeaver.Mesh;

/// <summary>
/// One Microsoft 365 mailbox a person has connected to the Executive Assistant: its account id, the
/// address it signed in as (its label, and what the assistant's <c>mailbox</c> argument matches), and
/// what is known about its grant.
///
/// <para>A person may connect SEVERAL mailboxes (a work and a personal one). Each is its own consent
/// and its own <see cref="EaCredential"/> record; see <see cref="EaMailboxes"/> for the id rules and
/// <c>Doc/AI/ExecutiveAssistant</c> for the design.</para>
/// </summary>
/// <param name="AccountId"><see cref="EaMailboxes.Primary"/> for the grant written before several
/// mailboxes existed (and for a person's first consent); <c>ea-credential--{10 hex}</c> for every
/// mailbox added since.</param>
/// <param name="Address">The signed-in address, when the consent recorded one. A grant written before
/// addresses were recorded has none until it is next consented.</param>
/// <param name="Access">What is known about this mailbox's grant. Never carries a token.</param>
public sealed record EaMailbox(string AccountId, string? Address, EaGraphAccess Access)
{
    /// <summary>True for the primary mailbox — the one written before several mailboxes existed.</summary>
    public bool IsPrimary => EaMailboxes.IsPrimary(AccountId);
}

/// <summary>
/// The account-id rules for a person's Microsoft 365 mailboxes — the same rules the credential-set
/// connections follow (MeshWeaver.Plugins#3464): the record that existed first keeps its id, every
/// account added since gets a fresh random id, and no id is ever reused.
/// </summary>
public static class EaMailboxes
{
    /// <summary>The primary mailbox's account id — the grant at the person's original credential path.</summary>
    public const string Primary = "primary";

    /// <summary>The account value that asks the consent flow to connect ANOTHER mailbox.</summary>
    public const string New = "new";

    /// <summary>The query parameter of the consent route that names the account (<c>?account=new</c>).</summary>
    public const string AccountParameter = "account";

    /// <summary>The prefix of every added mailbox's account id.</summary>
    public const string AddedPrefix = "ea-credential--";

    private static readonly Regex AddedId = new("^ea-credential--[0-9a-f]{10}$", RegexOptions.CultureInvariant);

    /// <summary>A fresh account id for an added mailbox: <c>ea-credential--{10 random lowercase hex}</c>.</summary>
    public static string NewAccountId() =>
        AddedPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();

    /// <summary>True for the primary id, and for null/blank (which callers use to mean "the primary").</summary>
    public static bool IsPrimary(string? account) =>
        string.IsNullOrWhiteSpace(account) || string.Equals(account, Primary, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="account"/> is EXACTLY the shape of an added mailbox id. The id ends
    /// up in a node path, and it arrives from a query string and an OAuth <c>state</c>, so anything
    /// else is refused rather than half-parsed.
    /// </summary>
    public static bool IsAddedAccountId(string? account) => account is not null && AddedId.IsMatch(account);

    /// <summary>True for <see cref="Primary"/>, <see cref="New"/> or a well-formed added id — the only values a consent may carry.</summary>
    public static bool IsValidRequest(string? account) =>
        IsPrimary(account) || string.Equals(account, New, StringComparison.OrdinalIgnoreCase) || IsAddedAccountId(account);

    /// <summary>
    /// Picks the mailbox a request addresses. <paramref name="requested"/> null/blank → the DEFAULT
    /// mailbox: the primary when it is connected, else the first connected mailbox, else the primary
    /// (or the first listed) so the caller can still say why. Otherwise matched against the address
    /// (case-insensitive) and then the account id. Null when nothing matches.
    /// </summary>
    public static EaMailbox? Select(IReadOnlyList<EaMailbox> mailboxes, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return mailboxes.FirstOrDefault(m => m.IsPrimary && m.Access.IsConnected)
                   ?? mailboxes.FirstOrDefault(m => m.Access.IsConnected)
                   ?? mailboxes.FirstOrDefault(m => m.IsPrimary)
                   ?? mailboxes.FirstOrDefault();
        var wanted = requested.Trim();
        return mailboxes.FirstOrDefault(m => string.Equals(m.Address, wanted, StringComparison.OrdinalIgnoreCase))
               ?? mailboxes.FirstOrDefault(m => string.Equals(m.AccountId, wanted, StringComparison.OrdinalIgnoreCase))
               ?? (IsPrimary(wanted) ? mailboxes.FirstOrDefault(m => m.IsPrimary) : null);
    }

    /// <summary>
    /// Orders mailboxes for display and for <see cref="Select"/>: the primary first, then the added
    /// ones by address and id. Pure.
    /// </summary>
    public static ImmutableList<EaMailbox> Ordered(IEnumerable<EaMailbox> mailboxes) =>
        mailboxes
            .OrderBy(m => m.IsPrimary ? 0 : 1)
            .ThenBy(m => m.Address ?? "~", StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.AccountId, StringComparer.Ordinal)
            .ToImmutableList();
}
