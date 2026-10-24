using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.MailCopy;

/// <summary>One connected mail account and whether THIS installation keeps a copy of it.</summary>
/// <param name="Provider">The provider segment (<c>outlook</c>, <c>gmail</c>, …).</param>
/// <param name="AccountId">The provider's own id for the connected account (the Executive Assistant's mailbox account id for Outlook).</param>
/// <param name="Label">What the person recognises it by — the address, else the id.</param>
/// <param name="IsOn">Whether a copy of THIS account exists on this installation.</param>
public sealed record MailCopyAccount(string Provider, string AccountId, string Label, bool IsOn);

/// <summary>
/// One provider's side of the per-account copy switch: which accounts of the owner are connected, and
/// switching a copy of each on and off. Every mail provider module registers one; <see cref="MailCopySwitch"/>
/// gathers them.
/// </summary>
public interface IMailAccountCopy
{
    /// <summary>The provider segment this module copies.</summary>
    string Provider { get; }

    /// <summary>The owner's connected accounts of this provider, each with its switch. Cold; emits once.</summary>
    IObservable<ImmutableList<MailCopyAccount>> Accounts(string owner);

    /// <summary>Switches the copy of <paramref name="accountId"/> on (the window in days, 90 by default); false when the account is not connected. Cold.</summary>
    IObservable<bool> Enable(string owner, string accountId, int windowDays);

    /// <summary>Switches the copy of <paramref name="accountId"/> off and deletes it (messages, attachment files, index). Cold; true when something was removed.</summary>
    IObservable<bool> Disable(string owner, string accountId);

    /// <summary>The owners this process has synced or been asked to — what the keeper's timer revisits.</summary>
    IReadOnlyCollection<string> KnownOwners { get; }

    /// <summary>Starts a (throttled) sync of every account of the owner whose copy is ON on this installation. Reads only; a copy that is off writes nothing.</summary>
    void KickAll(string owner);
}

/// <summary>
/// The per-installation, per-account switch of the mail copy — the one surface the onboarding flow and the
/// settings tab call.
///
/// <para>🚨 <b>A copy exists only where the person switched it on, on THIS installation.</b> A connected
/// account never starts one by itself, and an account whose credential lives elsewhere (a mother instance
/// holding a Google grant for a child) implies no copy here. Nothing in this interface defaults to on. The
/// caller must have established that the viewer IS the owner; the switch itself does not know who is asking.</para>
/// </summary>
public interface IMailCopySwitch
{
    /// <summary>
    /// What onboarding offers — "Keep a copy on this installation?": every connected account of every
    /// provider with its current switch, all off until someone says otherwise. Cold; emits once.
    /// </summary>
    IObservable<ImmutableList<MailCopyAccount>> Offer(string owner);

    /// <summary>Whether this installation keeps a copy of the account. Cold; emits once.</summary>
    IObservable<bool> IsOn(string owner, string provider, string accountId);

    /// <summary>Switches the copy on for the account on THIS installation. False when the account is unknown or not connected.</summary>
    IObservable<bool> Enable(string owner, string provider, string accountId, int windowDays = MailCopyNodeTypes.WindowDays);

    /// <summary>Switches the copy off and deletes it. False when there was nothing to remove.</summary>
    IObservable<bool> Disable(string owner, string provider, string accountId);
}

/// <summary>The switch over every registered provider.</summary>
/// <param name="providers">The providers' sides; one per installed mail module.</param>
public sealed class MailCopySwitch(IEnumerable<IMailAccountCopy> providers) : IMailCopySwitch
{
    private readonly ImmutableList<IMailAccountCopy> providers = [.. providers];

    /// <inheritdoc />
    public IObservable<ImmutableList<MailCopyAccount>> Offer(string owner)
        => providers.Count == 0
            ? Observable.Return(ImmutableList<MailCopyAccount>.Empty)
            : providers.Select(p => p.Accounts(owner).Take(1)
                    // One provider that cannot be read must not hide the others' accounts.
                    .Catch((Exception _) => Observable.Return(ImmutableList<MailCopyAccount>.Empty)))
                .Merge()
                .ToList()
                .Select(lists => lists.SelectMany(l => l)
                    .OrderBy(a => a.Provider, StringComparer.Ordinal).ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)
                    .ToImmutableList());

    /// <inheritdoc />
    public IObservable<bool> IsOn(string owner, string provider, string accountId)
        // A provider that cannot be read is an ERROR here, never "off": an unverified off state must not be reported.
        => Find(provider) is { } p
            ? p.Accounts(owner).Take(1).Select(accounts => accounts.Any(a => string.Equals(a.AccountId, accountId, StringComparison.Ordinal) && a.IsOn))
            : Observable.Return(false);

    /// <inheritdoc />
    public IObservable<bool> Enable(string owner, string provider, string accountId, int windowDays = MailCopyNodeTypes.WindowDays)
        => Find(provider) is { } p ? p.Enable(owner, accountId, windowDays) : Observable.Return(false);

    /// <inheritdoc />
    public IObservable<bool> Disable(string owner, string provider, string accountId)
        => Find(provider) is { } p ? p.Disable(owner, accountId) : Observable.Return(false);

    private IMailAccountCopy? Find(string provider)
        => providers.FirstOrDefault(p => string.Equals(p.Provider, provider, StringComparison.Ordinal));
}

