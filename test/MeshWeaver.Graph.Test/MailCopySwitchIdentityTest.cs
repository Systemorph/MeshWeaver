using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Graph.MailCopy;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <see cref="MailCopySwitch.Offer"/> CALLS every provider when it is called, under the requester's identity —
/// never when its cold result is subscribed. A provider reads the identity at its call (the engine and the stores
/// do), so a lazy enumeration would judge the subscriber instead: a lost identity empties the owner's offer, and
/// a privileged subscriber authorizes somebody else's read.
/// </summary>
public sealed class MailCopySwitchIdentityTest
{
    [Fact]
    public void Offer_CallsEveryProviderUnderTheRequestersIdentity_NotTheSubscribers()
    {
        var access = new AccessService();
        var outlook = new RecordingProvider("outlook", access);
        var gmail = new RecordingProvider("gmail", access);
        var offers = new MailCopySwitch([outlook, gmail]);

        IObservable<ImmutableList<MailCopyAccount>> offer;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = "owner", Name = "owner" }))
            offer = offers.Offer("owner");

        ImmutableList<MailCopyAccount>? listed = null;
        using (access.SwitchAccessContext(new AccessContext { ObjectId = "mallory", Name = "mallory" }))
            offer.Subscribe(l => listed = l);

        outlook.CalledAs.Should().Equal(["owner"], "the provider is called when the offer is asked for, as the person who asked");
        gmail.CalledAs.Should().Equal(["owner"], "every provider, not only the first");
        (listed?.Count ?? 0).Should().Be(2, "both providers' accounts are offered");
    }

    private sealed class RecordingProvider(string provider, AccessService access) : IMailAccountCopy
    {
        private ImmutableList<string?> calledAs = [];
        public ImmutableList<string?> CalledAs => calledAs;
        public string Provider => provider;
        public IReadOnlyCollection<string> KnownOwners => [];

        public IObservable<ImmutableList<MailCopyAccount>> Accounts(string owner)
        {
            calledAs = calledAs.Add(access.Context?.ObjectId);
            return Observable.Return(ImmutableList.Create(new MailCopyAccount(provider, "acct", "Mail", false)));
        }

        public IObservable<bool> Enable(string owner, string accountId, int windowDays) => Observable.Return(false);
        public IObservable<bool> Disable(string owner, string accountId) => Observable.Return(false);
        public void KickAll(string owner) { }
    }
}
