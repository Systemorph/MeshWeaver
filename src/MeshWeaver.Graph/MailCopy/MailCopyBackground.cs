using System.Collections.Immutable;
using System.Reactive;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.MailCopy;

/// <summary>
/// The stores as the BACKGROUND sync calls them: every call DECLARED System on the owner's behalf, at the
/// call itself. The stores refuse a call that carries no identity (<see cref="MailCopyAuthority"/>), and a
/// sync's later calls run after provider hops that keep no ambient identity — so the sync never relies on
/// one: each call opens its own System scope, and the store reads that scope when it is called.
/// A host without an <see cref="AccessService"/> has no access control and is passed straight through.
/// </summary>
internal sealed class BackgroundMailCopyStore(IMailCopyStore inner, AccessService? access) : IMailCopyStore
{
    private IObservable<T> AsSystem<T>(string owner, Func<IObservable<T>> call)
        => access.RunAsSystemFor(governedBy: null, onBehalfOf: owner, call);

    public IObservable<MailCopySource?> Source(string owner, string provider)
        => AsSystem(owner, () => inner.Source(owner, provider));

    public IObservable<MailCopySource> Create(string owner, MailCopySource source)
        => AsSystem(owner, () => inner.Create(owner, source));

    public IObservable<MailCopySource?> Commit(string owner, string provider, string generation, Func<MailCopySource, MailCopySource?> change)
        => AsSystem(owner, () => inner.Commit(owner, provider, generation, change));

    public IObservable<bool> Disable(string owner, string provider)
        => AsSystem(owner, () => inner.Disable(owner, provider));

    public IObservable<MailMessageCopy> Upsert(string owner, MailMessageCopy copy)
        => AsSystem(owner, () => inner.Upsert(owner, copy));

    public IObservable<bool> WriteIfNewer(string owner, MailMessageCopy copy)
        => AsSystem(owner, () => inner.WriteIfNewer(owner, copy));

    public IObservable<(bool Wrote, MailMessageCopy? Replaced)> ReplaceIfNewer(string owner, MailMessageCopy copy)
        => AsSystem(owner, () => inner.ReplaceIfNewer(owner, copy));

    public IObservable<long?> Revision(string owner, string provider, string generation, string messageId)
        => AsSystem(owner, () => inner.Revision(owner, provider, generation, messageId));

    public IObservable<bool> WriteIfUnchanged(string owner, MailMessageCopy copy, long? expected)
        => AsSystem(owner, () => inner.WriteIfUnchanged(owner, copy, expected));

    public IObservable<bool> SetAttachments(string owner, string provider, string generation, string messageId,
        ImmutableList<MailAttachmentCopy> attachments)
        => AsSystem(owner, () => inner.SetAttachments(owner, provider, generation, messageId, attachments));

    public IObservable<bool> Remove(string owner, string provider, string generation, string messageId)
        => AsSystem(owner, () => inner.Remove(owner, provider, generation, messageId));

    public IObservable<int> RemoveGeneration(string owner, string provider, string generation)
        => AsSystem(owner, () => inner.RemoveGeneration(owner, provider, generation));

    public IObservable<ImmutableList<MailMessageCopy>> List(string owner, string provider, int max, string? exceptGeneration = null)
        => AsSystem(owner, () => inner.List(owner, provider, max, exceptGeneration));

    public IObservable<MailMessageCopy?> Read(string owner, string provider, string generation, string messageId)
        => AsSystem(owner, () => inner.Read(owner, provider, generation, messageId));

    public IObservable<ImmutableList<MailMessageCopy>> ReadPage(
        string owner, string provider, string generation, long? from, int size, bool oldestFirst = false)
        => AsSystem(owner, () => inner.ReadPage(owner, provider, generation, from, size, oldestFirst));
}

/// <inheritdoc cref="BackgroundMailCopyStore"/>
internal sealed class BackgroundMailAttachmentStore(IMailAttachmentStore inner, AccessService? access) : IMailAttachmentStore
{
    public IObservable<string> Save(string owner, string copyPath, string relativePath, byte[] bytes)
        => access.RunAsSystemFor(governedBy: null, onBehalfOf: owner, () => inner.Save(owner, copyPath, relativePath, bytes));

    public IObservable<Unit> DeleteAll(string owner, string copyPath)
        => access.RunAsSystemFor(governedBy: null, onBehalfOf: owner, () => inner.DeleteAll(owner, copyPath));
}
