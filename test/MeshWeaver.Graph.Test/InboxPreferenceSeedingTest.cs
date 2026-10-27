using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The per-kind state filters the inbox seeds into a person's <see cref="InboxPreferences"/> node (the
/// <c>st:activity</c>, <c>st:mail</c>, <c>st:chat</c> keys of issue #7260) PERSIST: opening the inbox writes what the
/// node lacks, and a later session reads it back from the node. A choice the person already made is never overwritten
/// (the negative control: a seeding that wrote the defaults over everything would pass the persistence halves).
/// </summary>
public class InboxPreferenceSeedingTest : MonolithMeshTestBase
{
    private readonly InboxLogCapture _logs = new();

    /// <summary>Registers the capture AFTER the base constructor's ClearProviders(), beside the xUnit sink.</summary>
    public InboxPreferenceSeedingTest(ITestOutputHelper output) : base(output)
        => Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(_logs));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private static bool HoldsEveryStateFilter(InboxPreferences p)
        => p.ActivityStates.Count > 0 && p.MailStates.Count > 0 && p.ChatStates.Count > 0;

    /// <summary>
    /// A fresh person with no preferences node: the first open creates it, and the node holds every per-kind state
    /// filter — read back by a fresh read of the node, not from the page's own view of it.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AFreshPerson_GetsEveryStateFilterStored()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-seed-fresh";
        await CreateUserAsync(owner, ct);

        await RowsListed(Open(owner), ct);

        var stored = await InboxPrefs.ReadAsync(Mesh, owner, HoldsEveryStateFilter, ct);
        Flat(stored.ActivityStates).Should().Equal(Flat(InboxStateKind.Defaults(InboxStateKind.Activity)));
        Flat(stored.MailStates).Should().Equal(Flat(InboxStateKind.Defaults(InboxStateKind.Mail)));
        Flat(stored.ChatStates).Should().Equal(Flat(InboxStateKind.Defaults(InboxStateKind.Chat)));
    }

    /// <summary>
    /// The production shape of #7260: a node that exists but predates the per-kind state filters (the legacy global
    /// filter held, no activity or chat filter). Opening the inbox seeds them INTO the node, and a second session — a new page on the same person —
    /// reads them back from it. The mail filter the person already chose is the negative control: it survives the
    /// seeding unchanged.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task ANodeWithoutStateFilters_IsCompletedOnOpen_AndKeepsAnExistingChoice()
    {
        var ct = TestContext.Current.CancellationToken;
        const string owner = "inbox-seed-legacy";
        await CreateUserAsync(owner, ct);
        var chosenMail = ImmutableDictionary<string, bool>.Empty.Add(InboxStateGroup.Unread, false).Add(InboxStateGroup.Read, true);
        // As the live node held it before the per-kind filters: the LEGACY global filter (open AND done), no activity or
        // chat filter — and mail chosen by the person.
        var legacyStates = ImmutableDictionary<string, bool>.Empty.Add(InboxStateGroup.Open, true).Add(InboxStateGroup.Done, true);
        await InboxPrefs.SetAsync(Mesh, owner, p => p with { MailStates = chosenMail, States = legacyStates }, ct);
        var before = await InboxPrefs.ReadAsync(Mesh, owner, _ => true, ct);
        InboxPreferencesNodeType.MissingStates(before).Should().Equal([InboxStateKind.Activity, InboxStateKind.Chat],
            "the precondition: the node lacks exactly the activity and chat filters");

        await RowsListed(Open(owner), ct);

        var seeded = await InboxPrefs.ReadAsync(Mesh, owner, HoldsEveryStateFilter, ct);
        Flat(seeded.ActivityStates).Should().Equal(Flat(legacyStates), "the legacy choice moves to the activities filter, not the default");
        seeded.States.Should().BeEmpty("the legacy filter is emptied once migrated");
        Flat(seeded.ChatStates).Should().Equal(Flat(InboxStateKind.Defaults(InboxStateKind.Chat)));
        Flat(seeded.MailStates).Should().Equal(Flat(chosenMail), "a choice the person made is never overwritten by the seeding");

        // A new session reads what the first one wrote.
        await RowsListed(Open(owner), ct);
        var reread = await InboxPrefs.ReadAsync(Mesh, owner, HoldsEveryStateFilter, ct);
        InboxPreferencesNodeType.MissingStates(reread).Should().BeEmpty("the keys outlive the session that wrote them");
        Flat(reread.MailStates).Should().Equal(Flat(chosenMail));
        _logs.Lines.Should().BeEmpty("a seeding write that landed is neither a failed write nor a loop");
    }

    /// <summary>
    /// The production shape of #7260 (<c>rbuergi/_Settings/Inbox</c> v2): every kind and source seeded, the per-kind
    /// state filters empty and the LEGACY global filter still held. Exactly the three <c>st:</c> keys are missing, and
    /// completing the node migrates the legacy choice into the activities filter and empties it.
    /// </summary>
    [Fact]
    public void ALegacyNode_LacksExactlyTheThreeStateKeys_AndCompletingItMigratesTheLegacyFilter()
    {
        var legacy = new InboxPreferences
        {
            Kinds = ImmutableDictionary<string, bool>.Empty.Add(InboxRows.SourceKey("Mail"), true),
            Sources = ImmutableDictionary<string, bool>.Empty.Add(InboxRows.SourceKey("Mail"), true),
            States = ImmutableDictionary<string, bool>.Empty.Add(InboxStateGroup.Open, true).Add(InboxStateGroup.Done, false),
        };
        var expected = legacy with { States = ImmutableDictionary<string, bool>.Empty };
        var completed = InboxPreferencesNodeType.Completed(legacy, expected);

        completed.Should().NotBeNull();
        InboxLayoutArea.MissingKeys(legacy, completed!).Should().Equal(["st:activity", "st:chat", "st:mail"]);
        InboxLayoutArea.MissingKeys(completed!, completed!).Should().BeEmpty("the completed node lacks nothing — the next pass writes nothing");
        InboxPreferencesNodeType.Completed(completed!, expected).Should().BeNull("completing converges");
        completed!.States.Should().BeEmpty();
    }

    /// <summary>
    /// The session guard counts a set of keys as written only once the write is CONFIRMED. While it is in flight a
    /// second pass neither writes again nor counts as a loop; a failed write is released so a later pass retries; a
    /// confirmed one is never begun again — the negative control, without which "never written twice" would be untested.
    /// </summary>
    [Fact]
    public void TheSeedGuard_RecordsOnlyAConfirmedWrite()
    {
        const string keys = "st:activity,st:chat,st:mail";
        var guard = new InboxSeedGuard();

        guard.TryBegin(keys).Should().BeTrue("the first pass writes");
        guard.TryBegin(keys).Should().BeFalse("a pass while the write is in flight does not write again");
        guard.IsConfirmed(keys).Should().BeFalse("in flight is not written");

        guard.Release(keys);
        guard.TryBegin(keys).Should().BeTrue("a failed write is written again by a later pass");

        guard.Confirm(keys);
        guard.IsConfirmed(keys).Should().BeTrue();
        guard.Release(keys);
        guard.IsConfirmed(keys).Should().BeTrue("a late failure never un-confirms a committed write");
        guard.TryBegin(keys).Should().BeFalse("a confirmed write is never repeated");
    }

    /// <summary>A state filter as ordered "group=ticked" lines, for an order-free comparison.</summary>
    private static string[] Flat(IDictionary<string, bool> states)
        => [.. states.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")];

    private ISynchronizationStream<JsonElement> Open(string owner)
    {
        TestUsers.DevLogin(Mesh, new AccessContext { ObjectId = owner, Name = owner, Email = $"{owner}@meshweaver.io" });
        return GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(owner), new LayoutAreaReference(InboxLayoutArea.AreaName));
    }

    /// <summary>Waits until the page has fed its grid once — the rows pass is what seeds the node.</summary>
    private static async Task RowsListed(ISynchronizationStream<JsonElement> stream, CancellationToken ct)
        => await stream.GetDataStream<JsonElement>(new JsonPointerReference(LayoutAreaReference.GetDataPointer(InboxRows.GridId)))
            .Where(r => r.ValueKind == JsonValueKind.Array)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private async Task CreateUserAsync(string path, CancellationToken ct)
        => await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(MeshNode.FromPath(path) with
            {
                NodeType = UserNodeType.NodeType, Name = path, State = MeshNodeState.Active,
                Content = new User { FullName = path },
            }))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    /// <summary>The inbox page's own warnings and errors — a seeding pass that cries "loop" or "could not write" is a finding.</summary>
    private sealed class InboxLogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> lines = new();

        /// <summary>Every warning or error the inbox page logged.</summary>
        public string[] Lines => [.. lines];

        public ILogger CreateLogger(string categoryName) => new Capturing(categoryName == typeof(InboxLayoutArea).FullName ? lines : null);

        public void Dispose() { }

        private sealed class Capturing(ConcurrentQueue<string>? sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => sink is not null && logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    sink!.Enqueue($"{logLevel}: {formatter(state, exception)} {exception?.Message}");
            }
        }
    }
}
