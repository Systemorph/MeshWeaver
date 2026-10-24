using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Graph.MailCopy;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Code that stands in for a NodeType's compiled source: it builds its OWN engine over its OWN store and switches
/// the copy on through each public road. Loaded from a copy of this assembly in a collectible context, it is
/// in-mesh code to the impersonation guard.
/// </summary>
public static class InMeshMailCopyCode
{
    /// <summary>Through the engine.</summary>
    public static object ViaEngine(AccessService access, IMailCopyStore store)
        => new MailCopyEngine(new OneAccountFeed(), store, access: access).Enable("owner", 30);

    /// <summary>Through the per-account copy.</summary>
    public static object ViaAccountCopy(AccessService access, IMailCopyStore store)
    {
        var feed = new OneAccountFeed();
        return new MailFeedAccountCopy(new MailCopyEngine(feed, store, access: access), feed).Enable("owner", "acct", 30);
    }

    /// <summary>Through the switch over the providers.</summary>
    public static object ViaSwitch(AccessService access, IMailCopyStore store)
    {
        var feed = new OneAccountFeed();
        return new MailCopySwitch([new MailFeedAccountCopy(new MailCopyEngine(feed, store, access: access), feed)])
            .Enable("owner", "outlook", "acct", 30);
    }

    private sealed class OneAccountFeed : IMailCopyFeed
    {
        public string Provider => "outlook";
        public IObservable<string> AccountId(string owner) => Observable.Return("acct");
        public IObservable<ImmutableList<(string AccountId, string Label)>> Accounts(string owner) => Observable.Return(ImmutableList.Create(("acct", "Mail")));
        public IObservable<MailFeedStep> Step(string owner, string? cursor, DateTimeOffset windowStart) => Observable.Empty<MailFeedStep>();
        public IObservable<IMailAttachmentSource> Attachments(string owner) => Observable.Empty<IMailAttachmentSource>();
    }
}

/// <summary>
/// 🚨 Switching the copy on starts a System backfill, which calls the engine's STORE as System. An in-mesh caller
/// that supplies its own store must therefore be judged at <see cref="MailCopyEngine.Enable"/> and at each wrapper,
/// exactly as at <see cref="MailCopyEngine.Kick"/> — never pass because the kick is issued from this platform
/// assembly. Under Enforce the in-mesh call is refused and its store's callbacks never run as System.
///
/// <para>Negative control: the IDENTICAL methods called from the default load context are not refused, so the
/// refusal is the guard's judgement of the calling code, not a call that could never succeed.</para>
/// </summary>
public sealed class MailCopyEnableGuardTest : IDisposable
{
    private const string UserNodeType = "DynamicNode_rbuergi_Evil";
    private readonly List<AssemblyLoadContext> contexts = [];

    public void Dispose()
    {
        foreach (var context in contexts)
            context.Unload();
    }

    private static AccessService Enforcing()
    {
        var access = new AccessService(null, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [InMeshImpersonationGuard.ModeKey] = InMeshImpersonationMode.Enforce })
            .Build());
        access.SetContext(new AccessContext { ObjectId = "owner", Name = "owner" });
        return access;
    }

    private Type InMeshCopy()
    {
        var context = new AssemblyLoadContext(UserNodeType, isCollectible: true);
        contexts.Add(context);
        var copy = context.LoadFromAssemblyPath(typeof(InMeshMailCopyCode).Assembly.Location);
        AssemblyLoadContext.GetLoadContext(copy).Should().BeSameAs(context, "the copy must really live in the in-mesh context");
        return copy.GetType(typeof(InMeshMailCopyCode).FullName!, throwOnError: true)!;
    }

    /// <summary>Calls <paramref name="method"/> and subscribes what it returns; the exception it ended in, or null.</summary>
    private static Exception? CallAndSubscribe(Type code, string method, AccessService access, IMailCopyStore store)
    {
        try
        {
            var result = code.GetMethod(method)!.Invoke(null, [access, store]);
            Exception? failure = null;
            switch (result)
            {
                case IObservable<MailCopySource> source: source.Subscribe(_ => { }, ex => failure = ex); break;
                case IObservable<bool> flag: flag.Subscribe(_ => { }, ex => failure = ex); break;
            }
            return failure;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return ex.InnerException;
        }
    }

    [Theory]
    [InlineData(nameof(InMeshMailCopyCode.ViaEngine))]
    [InlineData(nameof(InMeshMailCopyCode.ViaAccountCopy))]
    [InlineData(nameof(InMeshMailCopyCode.ViaSwitch))]
    public void InMeshCode_CannotSwitchACopyOn_OverItsOwnStore(string road)
    {
        var access = Enforcing();
        var store = new RecordingStore(access);

        var failure = CallAndSubscribe(InMeshCopy(), road, access, store);

        failure.Should().BeOfType<InMeshImpersonationRefusedException>("the in-mesh code that asked is judged, not this platform assembly");
        store.CalledAsSystem.Should().BeFalse("the caller-supplied store's callbacks never ran as System");
    }

    [Theory]
    [InlineData(nameof(InMeshMailCopyCode.ViaEngine))]
    [InlineData(nameof(InMeshMailCopyCode.ViaAccountCopy))]
    [InlineData(nameof(InMeshMailCopyCode.ViaSwitch))]
    public void PlatformCode_CanSwitchACopyOn(string road)
    {
        var access = Enforcing();
        var store = new RecordingStore(access);

        var failure = CallAndSubscribe(typeof(InMeshMailCopyCode), road, access, store);

        failure.Should().BeNull("the same call from the default load context is platform code");
        store.Created.Should().BeTrue("the switch-on reached the store");
    }

    /// <summary>A store that records whether any call reached it under System; it answers every call empty.</summary>
    private sealed class RecordingStore(AccessService access) : IMailCopyStore
    {
        private volatile bool asSystem;
        private volatile bool created;

        public bool CalledAsSystem => asSystem;
        public bool Created => created;

        private IObservable<T> Seen<T>(IObservable<T> answer) => Observable.Defer(() =>
        {
            if (access.Context?.ObjectId == AccessService.SystemObjectId)
                asSystem = true;
            return answer;
        });

        public IObservable<MailCopySource?> Source(string owner, string provider) => Seen(Observable.Return<MailCopySource?>(null));
        public IObservable<MailCopySource> Create(string owner, MailCopySource source)
        {
            created = true;
            return Seen(Observable.Return(source));
        }
        public IObservable<MailCopySource?> Commit(string owner, string provider, string generation, Func<MailCopySource, MailCopySource?> change) => Seen(Observable.Return<MailCopySource?>(null));
        public IObservable<bool> Disable(string owner, string provider) => Seen(Observable.Return(false));
        public IObservable<MailMessageCopy> Upsert(string owner, MailMessageCopy copy) => Seen(Observable.Return(copy));
        public IObservable<bool> WriteIfNewer(string owner, MailMessageCopy copy) => Seen(Observable.Return(false));
        public IObservable<(bool Wrote, MailMessageCopy? Replaced)> ReplaceIfNewer(string owner, MailMessageCopy copy) => Seen(Observable.Return((false, (MailMessageCopy?)null)));
        public IObservable<long?> Revision(string owner, string provider, string generation, string messageId) => Seen(Observable.Return<long?>(null));
        public IObservable<bool> WriteIfUnchanged(string owner, MailMessageCopy copy, long? expected) => Seen(Observable.Return(false));
        public IObservable<bool> SetAttachments(string owner, string provider, string generation, string messageId, ImmutableList<MailAttachmentCopy> attachments) => Seen(Observable.Return(false));
        public IObservable<bool> Remove(string owner, string provider, string generation, string messageId) => Seen(Observable.Return(false));
        public IObservable<int> RemoveGeneration(string owner, string provider, string generation) => Seen(Observable.Return(0));
        public IObservable<ImmutableList<MailMessageCopy>> List(string owner, string provider, int max, string? exceptGeneration = null) => Seen(Observable.Return(ImmutableList<MailMessageCopy>.Empty));
        public IObservable<MailMessageCopy?> Read(string owner, string provider, string generation, string messageId) => Seen(Observable.Return<MailMessageCopy?>(null));
        public IObservable<ImmutableList<MailMessageCopy>> ReadPage(string owner, string provider, string generation, long? from, int size, bool oldestFirst = false) => Seen(Observable.Return(ImmutableList<MailMessageCopy>.Empty));
    }
}
