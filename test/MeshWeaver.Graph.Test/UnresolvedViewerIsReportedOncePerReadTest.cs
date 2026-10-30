using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>An unresolved viewer is reported ONCE per read.</b> A secured read aimed into a named
/// partition that no viewer names falls back to Anonymous, and
/// <c>QueryIdentityResolver.ResolveAndReport</c> says so in one warning naming the query.
///
/// <para>#7565 added the access follow of an open secured query, and the follow resolved the
/// viewer through the REPORTING resolver as well — so every such read logged the same warning
/// twice, and MeshWeaver.Plugins' <c>QueryIdentityTest</c> (which asserts a single warning) turned
/// that repository's main red. The follow rides along the read; it is not a second read, and it
/// now resolves the same viewer without reporting.</para>
/// </summary>
public class UnresolvedViewerIsReportedOncePerReadTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string OwnerPartition = "OwnerHome";
    private const string PrivatePath = "OwnerHome/Secret";

    private readonly UnresolvedViewerCapture capture = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        // ConfigureMeshBase: the default chain grants Public a role at every partition root, which
        // would make the Anonymous read below see the node.
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(new MeshNode(OwnerPartition) { Name = "Owner Home" })
            .ConfigureServices(s => s.AddLogging(l =>
                l.Services.AddSingleton<ILoggerProvider>(capture)));

    [Fact(Timeout = 60_000)]
    public async Task AnUnresolvedSecuredRead_LogsItsDiagnosticExactlyOnce()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Created at runtime, so the storage provider (with its access follow) serves it rather than
        // the static catalog.
        await meshService
            .CreateNode(MeshNode.FromPath(PrivatePath) with
            {
                Name = "Secret",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            })
            .FirstAsync()
            .Timeout(30.Seconds())
            .Await(TestContext.Current.CancellationToken);

        capture.Clear();

        IReadOnlyList<MeshNode> rows;
        access.ClearHostIdentity();
        access.SetContext(null);
        try
        {
            rows = (await meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{PrivatePath}"))
                .Where(c => c.ChangeType == QueryChangeType.Initial)
                .FirstAsync()
                .Timeout(20.Seconds())
                .Await(TestContext.Current.CancellationToken)).Items;
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }

        rows.Should().BeEmpty("an unidentified read answers with the Anonymous view, which here is nothing");
        capture.Warnings.Where(w => w.Contains(PrivatePath, StringComparison.Ordinal)).Should().ContainSingle(
            "one read, one diagnostic — the access follow of the open query must not report the "
            + $"same unresolved viewer again. Captured: [{string.Join(" | ", capture.Warnings)}]");
    }

    private sealed class UnresolvedViewerCapture : ILoggerProvider
    {
        private ImmutableQueue<string> warnings = ImmutableQueue<string>.Empty;

        internal string[] Warnings => Volatile.Read(ref warnings).ToArray();

        internal void Clear() => Interlocked.Exchange(ref warnings, ImmutableQueue<string>.Empty);

        internal void Add(string message) => ImmutableInterlocked.Enqueue(ref warnings, message);

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose() { }

        private sealed class CapturingLogger(UnresolvedViewerCapture sink) : ILogger
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
                    sink.Add(message);
            }
        }
    }
}
