using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 <b>#2454 — a handler that entered, exited <c>Processed</c>, and never replied.</b>
///
/// <para><c>HandleCreateOrUpdateNodeRequest</c> subscribed to its existing-node read with TWO arms —
/// a value and a fault — and returned <c>request.Processed()</c> immediately. A source that
/// <b>completes without emitting</b> matches neither arm, so no response was ever posted while the
/// delivery had already been marked handled. The caller then waits out its entire budget.</para>
///
/// <para>That is #2454's signature verbatim, and it is not theoretical: measured on shard 3 as a
/// per-node hub that could not quiesce —
/// <c>PendingCallbacks=1[CreateOrUpdateNodeRequest] … HANDLER_ENTER → HANDLER_EXIT state=Processed
/// ⇒ a handler was entered and no reply, completion or fault has been recorded since</c> — holding
/// a whole mesh disposal open for 19 s.</para>
///
/// <para><b>Why the obvious repair is wrong.</b> <c>DefaultIfEmpty()</c> emits <c>null</c>, which
/// this handler's value arm reads as "the node does not exist" and turns into a CREATE. An empty
/// read means the read produced no answer — not that the node is absent — so inventing a create
/// from it would be a silent write on missing information. Empty gets its own outcome, and that
/// outcome is a refusal.</para>
/// </summary>
public class UpsertEmptyReadIsAnsweredTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The marker a path carries to make the decorated adapter's Read complete empty.</summary>
    private const string EmptyReadMarker = "empty-read-2454";

    /// <summary>
    /// Forwards everything to the real adapter except <see cref="Read"/> for a marked path, which
    /// completes WITHOUT emitting — the third terminal state. Not a mock of the mesh: the real
    /// adapter does all the work, and exactly one call on one path is diverted.
    /// </summary>
    private sealed class EmptyReadOnMarkedPath(IStorageAdapter inner) : IStorageAdapter
    {
        public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
            => path.Contains(EmptyReadMarker, StringComparison.Ordinal)
                ? Observable.Empty<MeshNode?>()
                : inner.Read(path, options);

        public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
            => inner.Write(node, options);

        public IObservable<string> Delete(string path) => inner.Delete(path);

        public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
            ListChildPaths(string? parentPath) => inner.ListChildPaths(parentPath);

        public IObservable<bool> Exists(string path) => inner.Exists(path);

        public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
            => inner.GetPartitionObjects(nodePath, subPath, options);

        public IObservable<Unit> SavePartitionObjects(
            string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
            => inner.SavePartitionObjects(nodePath, subPath, objects, options);

        public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
            => inner.DeletePartitionObjects(nodePath, subPath);

        public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
            => inner.GetPartitionMaxTimestamp(nodePath, subPath);
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                // Decorate whatever the mesh registered, so the guard/version-writing chain stays
                // intact and only the one Read is diverted.
                var existing = services.LastOrDefault(d => d.ServiceType == typeof(IStorageAdapter));
                if (existing is null)
                    return services;
                services.AddSingleton<IStorageAdapter>(sp =>
                    new EmptyReadOnMarkedPath(CreateInner(sp, existing)));
                return services;
            });

    private static IStorageAdapter CreateInner(IServiceProvider sp, ServiceDescriptor d)
        => d.ImplementationInstance as IStorageAdapter
           ?? d.ImplementationFactory?.Invoke(sp) as IStorageAdapter
           ?? (IStorageAdapter)ActivatorUtilities.CreateInstance(sp, d.ImplementationType!);

    /// <summary>
    /// 🚨 THE REGRESSION. On unfixed code this times out: no response is ever posted, because the
    /// read completed empty and the handler had already returned <c>Processed()</c>.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Upsert_WhenTheExistingReadCompletesEmpty_AnswersTheCaller()
    {
        var path = $"{TestPartition}/{EmptyReadMarker}-{Guid.NewGuid():N}";
        var node = MeshNode.FromPath(path) with { Name = "Empty read", NodeType = "Markdown" };

        var response = await ObserveNodeOperation<CreateOrUpdateNodeResponse>(
                new CreateOrUpdateNodeRequest(node))
            .Select(d => d.Message)
            .Should().Within(TimeSpan.FromSeconds(20)).Emit(
                "a handler that returns Processed() owes the caller a reply on EVERY terminal state "
                + "of its source — including 'completed without emitting' (MeshWeaver#2454)");

        // The POINT is that an answer arrives at all. Its content is secondary — but it must be a
        // refusal, never a silent success, because the handler never learned whether the node
        // existed and must not guess.
        response.Success.Should().BeFalse(
            "an empty read means the upsert could not decide between create and update; reporting "
            + "success would claim a write that never happened");
    }
}
