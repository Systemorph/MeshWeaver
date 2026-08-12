using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.AccessControl.Test;

/// <summary>
/// A secured read that names no viewer used to resolve, silently, to
/// <see cref="WellKnownUsers.Anonymous"/> — so a user's own private nodes came back as an EMPTY
/// result set, and "you have no records" is exactly how the caller reported it. Five user-visible
/// bugs in a single day were that one confusion (MeshWeaver.Plugins #360, #406, #415 and two in
/// #417: a "run from your own copy" redirect that had never once fired, a game-history grid
/// telling its owner there were no recorded games while six sat in storage).
///
/// <para>These tests pin the four halves of the fix:</para>
/// <list type="number">
///   <item>an unresolved viewer on a read aimed into a named partition is <b>diagnosed</b>, never
///     silent (<see cref="OwnPrivateNode_ReadWithNoAmbientIdentity_IsDiagnosedNotSilentlyEmpty"/>);</item>
///   <item>a read that declares it needs a real viewer <b>fails closed</b> with an exception rather
///     than answering with the Anonymous view
///     (<see cref="ReadThatRequiresAViewer_FailsClosed_RatherThanAnsweringAsAnonymous"/>);</item>
///   <item>a genuine mesh-wide public listing still works unstamped, silently
///     (<see cref="PublicListing_StillWorksUnstamped_AndIsNotDiagnosed"/>);</item>
///   <item>stamping a viewer never WIDENS what that viewer can read
///     (<see cref="StampingAViewer_NeverWidensAccess"/>).</item>
/// </list>
/// </summary>
public class QueryIdentityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string OwnerPartition = "AliceHome";
    private const string PrivatePath = "AliceHome/Secret";
    private const string PublicPath = "PublicCatalog/Listed";

    private readonly UnresolvedViewerCapture capture = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        // ConfigureMeshBase, NOT ConfigureMesh: the default chain grants Public an Admin role at
        // every partition root, which would make "can Anonymous see this?" unanswerable. Here only
        // the DevLogin admin (Roland) holds a real grant, so an Anonymous read genuinely sees
        // nothing — which is the whole point of the trap.
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            // Only the PARTITION ROOTS are static. The content nodes are created at runtime
            // (EnsureContentNodes) because a statically-seeded node is served by
            // StaticNodeQueryProvider, which applies no row-level security — it would answer every
            // viewer identically and make an access assertion meaningless.
            .AddMeshNodes(
                new MeshNode(OwnerPartition) { Name = "Alice Home" },
                new MeshNode("PublicCatalog") { Name = "Public Catalog" },
                // "Published to the web" has ONE definition in this codebase: an explicit Anonymous
                // Read grant. Seeded statically (rather than granted at runtime) so the public-listing
                // test asserts against the same shape production uses.
                new MeshNode(WellKnownUsers.Anonymous + "_Access", "PublicCatalog/_Access")
                {
                    NodeType = "AccessAssignment",
                    Name = "Anonymous — Viewer",
                    MainNode = "PublicCatalog",
                    Content = new AccessAssignment
                    {
                        AccessObject = WellKnownUsers.Anonymous,
                        DisplayName = WellKnownUsers.Anonymous,
                        Roles = [new RoleAssignment { Role = "Viewer" }]
                    },
                })
            .ConfigureServices(s => s.AddLogging(l =>
                l.Services.AddSingleton<ILoggerProvider>(capture)));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>
    /// Runs <paramref name="body"/> with NO ambient identity anywhere — the state every hub action
    /// block, Rx continuation, <c>IIoPool</c> worker and background service is in on a real portal
    /// (<c>AccessService.CircuitContext</c> deliberately resolves to null off the circuit's own call
    /// tree, so identity resolution fails closed instead of answering as a stranger). The xUnit host
    /// is a single-identity process, so its standing DevLogin identity has to be cleared to model
    /// that; it is restored unconditionally.
    /// </summary>
    private async Task<T> WithNoAmbientIdentity<T>(Func<Task<T>> body)
    {
        Access.ClearHostIdentity();
        Access.SetContext(null);
        try
        {
            return await body();
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }
    }

    /// <summary>
    /// Creates the two content nodes as the DevLogin admin, so they carry a real <c>CreatedBy</c>
    /// and are served by the RLS-applying storage provider rather than the static catalog.
    /// </summary>
    private async Task EnsureContentNodes()
    {
        foreach (var (path, name) in new[] { (PrivatePath, "Alice Secret"), (PublicPath, "Listed Item") })
            await MeshService
                .CreateNode(MeshNode.FromPath(path) with
                {
                    Name = name,
                    NodeType = "Markdown",
                    State = MeshNodeState.Active,
                })
                .FirstAsync()
                .Timeout(30.Seconds())
                .ToTask();
    }

    private async Task<IReadOnlyList<MeshNode>> RunQuery(MeshQueryRequest request) =>
        (await MeshService.Query<MeshNode>(request)
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .FirstAsync()
            .Timeout(20.Seconds())
            .ToTask()).Items;

    /// <summary>
    /// 🚨 THE trap, end to end. A read aimed at a user's own private node, issued from a context
    /// with no ambient identity, degrades to the Anonymous view and returns NOTHING — and before
    /// this fix it did so in total silence, so the caller could only report absence.
    ///
    /// <para>The fix does not change the result (that would WIDEN access — see
    /// <see cref="StampingAViewer_NeverWidensAccess"/>). It changes the SILENCE: the boundary that
    /// resolves the viewer now emits a warning naming the query and the two remedies. This test
    /// asserts on that warning, which is the fact that is red on main — where nothing is logged at
    /// all and the empty result is indistinguishable from a missing node.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task OwnPrivateNode_ReadWithNoAmbientIdentity_IsDiagnosedNotSilentlyEmpty()
    {
        await EnsureContentNodes();

        // Baseline: with an identity, the node IS visible. Without this the "empty" below could be
        // a missing fixture rather than an access verdict.
        var seen = await RunQuery(MeshQueryRequest.FromQuery($"path:{PrivatePath}"));
        seen.Select(n => n.Path).Should().Contain(PrivatePath,
            "the DevLogin admin holds a real grant, so the fixture must be readable WITH an identity");

        capture.Clear();

        var invisible = await WithNoAmbientIdentity(() =>
            RunQuery(MeshQueryRequest.FromQuery($"path:{PrivatePath}")));

        // The access verdict itself is unchanged and must stay unchanged: an unidentified reader
        // sees the Anonymous view, which here is nothing.
        invisible.Should().BeEmpty("an unidentified read must never be widened to the owner's view");

        // …but it is no longer SILENT. This is the assertion that fails on main.
        capture.Warnings.Should().ContainSingle(w => w.Contains(PrivatePath, StringComparison.Ordinal),
            "a read that fell back to Anonymous because NOTHING named a viewer must say so, naming "
            + "the query — otherwise the empty result reads as 'the record does not exist'. "
            + $"Captured warnings: [{string.Join(" | ", capture.Warnings)}]");

        var warning = capture.Warnings.Single(w => w.Contains(PrivatePath, StringComparison.Ordinal));
        Output.WriteLine(warning);
        warning.Should().Contain("AsPublicListing",
            "the diagnostic must name the remedy for a genuine public listing");
        warning.Should().Contain("RequireViewer",
            "…and the remedy for a read that must not answer as Anonymous");
    }

    /// <summary>
    /// The teeth of the design. <c>RequireViewer()</c> declares "an empty result here would be
    /// reported to a human as absence", so an unresolvable viewer becomes a
    /// <see cref="QueryIdentityUnresolvedException"/> instead of the Anonymous view. A thrown
    /// exception is diagnosable; an empty list is not.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ReadThatRequiresAViewer_FailsClosed_RatherThanAnsweringAsAnonymous()
    {
        await EnsureContentNodes();

        var thrown = await WithNoAmbientIdentity(async () =>
        {
            try
            {
                await RunQuery(MeshQueryRequest.FromQuery($"path:{PrivatePath}").RequireViewer());
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        thrown.Should().BeOfType<QueryIdentityUnresolvedException>(
            "a read that declared it needs a real viewer must fail closed, not hand back the "
            + "Anonymous view — which for a user's own space is an empty list that reads as absence");
        thrown!.Message.Should().Contain(PrivatePath);

        // …and it still resolves normally when an identity IS available, so the declaration costs
        // nothing on the happy path.
        var withIdentity = await RunQuery(MeshQueryRequest.FromQuery($"path:{PrivatePath}").RequireViewer());
        withIdentity.Select(n => n.Path).Should().Contain(PrivatePath);
    }

    /// <summary>
    /// 🚨 The case that must stay easy: a genuine mesh-wide PUBLIC listing. Stamping the viewer here
    /// would be WRONG — it folds each visitor's own private copies into the public list as duplicate
    /// entries (MeshWeaver.Plugins #415). So an unstamped public listing must keep working, and must
    /// NOT be nagged about.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task PublicListing_StillWorksUnstamped_AndIsNotDiagnosed()
    {
        await EnsureContentNodes();

        capture.Clear();

        var listed = await WithNoAmbientIdentity(() =>
            RunQuery(MeshQueryRequest.FromQuery($"path:{PublicPath}").AsPublicListing()));

        listed.Select(n => n.Path).Should().Contain(PublicPath,
            "a public listing evaluates as Anonymous, and Anonymous holds a Read grant here");
        capture.Warnings.Should().BeEmpty(
            "a DECLARED public listing has nothing to diagnose — Anonymous is the intended viewer, "
            + $"not a fallback. Captured: [{string.Join(" | ", capture.Warnings)}]");

        // The same query without the declaration is the ambiguous shape — it still answers
        // identically (never widened, never narrowed), it is merely no longer silent about it.
        capture.Clear();
        var undeclared = await WithNoAmbientIdentity(() =>
            RunQuery(MeshQueryRequest.FromQuery($"path:{PublicPath}")));
        // Declaring the intent must not change WHAT a read returns — only whether it is diagnosed.
        undeclared.Select(n => n.Path).OrderBy(p => p, StringComparer.Ordinal)
            .Should().Equal(listed.Select(n => n.Path).OrderBy(p => p, StringComparer.Ordinal));
        capture.Warnings.Should().NotBeEmpty(
            "the UNDECLARED form of the same read is exactly the ambiguous shape the diagnostic exists for");
    }

    /// <summary>
    /// Resolving identity earlier must never resolve it more generously. A viewer explicitly denied
    /// on a node still cannot see it when the request stamps them — the fix moves WHERE the viewer
    /// is decided, never WHAT they are allowed to read.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task StampingAViewer_NeverWidensAccess()
    {
        const string denied = "denied-dave";
        await EnsureContentNodes();

        var asDenied = await RunQuery(MeshQueryRequest.FromQuery($"path:{PrivatePath}").ForViewer(denied));
        asDenied.Should().BeEmpty(
            $"'{denied}' holds no grant on {PrivatePath}; stamping them must not widen the read");

        // Explicit-anonymous (the empty-string marker) is honoured identically by every backend now
        // — it used to mean "the anonymous visitor" on the pedestrian provider and "go look at the
        // ambient context" on the Postgres/Snowflake twins, so the SAME request answered differently
        // depending on which one served it. Here the ambient identity is the DevLogin ADMIN, so a
        // backend that consulted it would return the node.
        var asExplicitAnonymous = await RunQuery(MeshQueryRequest.FromQuery($"path:{PrivatePath}", ""));
        asExplicitAnonymous.Should().BeEmpty(
            "UserId=\"\" means the anonymous visitor — it must NOT fall through to the ambient admin");

        // …while the identity that does hold a grant still reads it.
        var asOwner = await RunQuery(
            MeshQueryRequest.FromQuery($"path:{PrivatePath}").ForViewer(TestUsers.Admin.ObjectId!));
        asOwner.Select(n => n.Path).Should().Contain(PrivatePath,
            "the grant holder must still see the node — failing closed must not fail shut");
    }

    /// <summary>
    /// Captures the unresolved-viewer warnings out of the REAL logging pipeline. Asserting on the
    /// log line is the only way to pin "the read is no longer silent" without re-implementing the
    /// resolver in the test.
    /// </summary>
    private sealed class UnresolvedViewerCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> warnings = new();

        internal string[] Warnings => warnings.ToArray();

        internal void Clear()
        {
            while (warnings.TryDequeue(out _)) { }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(warnings);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> sink) : ILogger
        {
            private sealed class NullScope : IDisposable
            {
                internal static readonly NullScope Instance = new();
                public void Dispose() { }
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Warning)
                    return;
                var message = formatter(state, exception);
                // The resolver's own wording — see QueryIdentityResolver.DescribeUnresolved.
                if (message.Contains("NO resolvable viewer", StringComparison.Ordinal))
                    sink.Enqueue(message);
            }
        }
    }
}
