using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A recursive delete must not be refused because an UNRELATED recycle disposed a leaf's per-node
/// hub while that leaf was answering.
///
/// <para><b>The production line</b>, memex.systemorph.com 2026-09-27 05:26:38–41Z, a governed
/// DeleteSpace of <c>UWDeepfieldData</c>:</para>
/// <code>
/// ValidationFailed: Cannot delete 'UWDeepfieldData/Clients/SafeDriveInsurance': Validation error:
///   Instances cannot be resolved and nested lifetimes cannot be created from this LifetimeScope
///   as it (or one of its parent scopes) has already been disposed.
/// </code>
/// <para>with ~15 <c>[ValidateDelete] … failed — treating as error</c> lines in the same seconds,
/// while the UWDeepfield NodeTypes minted new Release nodes. The pre-flight's own post activates
/// each leaf; the leaf hub resolved its validators from its scope AFTER an asynchronous storage
/// read, and a recycle that disposed the hub in between turned "this activation is going away"
/// into a validation verdict against the delete.</para>
///
/// <para><b>The repro is deterministic, not a race.</b> A deletion validator, on its first call for
/// one leaf, disposes that leaf's hub, waits for the disposal to complete, and then touches the
/// hub's scope exactly as a per-hub-scoped service does — which raises the genuine Autofac
/// <see cref="ObjectDisposedException"/>. The contract: the delete converges on the fresh
/// activation and removes the whole subtree. Two cases, one per half of the delete that addresses
/// the leaf: the pre-flight <c>ValidateDeleteRequest</c> and the commit's leaf
/// <c>DeleteNodeRequest</c>.</para>
/// </summary>
public class RecursiveDeleteSurvivesARecycledLeafTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string VictimId = "recycled";

    /// <summary>
    /// Small enough that the pre-fix shape (a leg that is refused or never answered) fails well
    /// inside the method timeout; every rung below is derived by <c>MeshOperationOptions.Nest</c>.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private readonly DisposesItsLeafOnceValidator validator = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureServices(services => services
            .AddSingleton<INodeValidator>(validator)
            .AddSingleton(new MeshOperationOptions { Timeout = Budget }));

    /// <summary>The leaf's hub dies while answering the pre-flight <c>ValidateDeleteRequest</c>.</summary>
    [Fact]
    public Task ALeafRecycledDuringThePreflight_DoesNotRefuseTheDelete()
        => DeleteSurvivesARecycleOnCall(1, "preflight-recycle");

    /// <summary>
    /// The leaf's hub dies while running its own leaf <c>DeleteNodeRequest</c> in the commit —
    /// the second validation of the same leaf.
    /// </summary>
    [Fact]
    public Task ALeafRecycledDuringTheCommit_DoesNotFailTheDelete()
        => DeleteSurvivesARecycleOnCall(2, "commit-recycle");

    private async Task DeleteSurvivesARecycleOnCall(int call, string rootId)
    {
        var ct = TestContext.Current.CancellationToken;
        var rootPath = $"{TestPartition}/{rootId}";
        var victimPath = $"{rootPath}/{VictimId}";
        var siblingPath = $"{rootPath}/sibling";

        await NodeFactory.CreateNode(
                new MeshNode(rootId, TestPartition) { Name = rootId, NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        await NodeFactory.CreateNode(
                new MeshNode(VictimId, rootPath) { Name = "Recycled", NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        await NodeFactory.CreateNode(
                new MeshNode("sibling", rootPath) { Name = "Sibling", NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);

        var mesh = Mesh;
        validator.Arm(victimPath, call, p => mesh.GetHostedHub(new Address(p), HostedHubCreation.Never));

        // Producer -> test signal carrying EITHER terminal, so a refusal is reported the moment it
        // happens instead of spending the whole success window in silence.
        var outcome = new AsyncSubject<(bool? Removed, Exception? Error)>();
        using var deleting = NodeFactory.DeleteNode(rootPath).Subscribe(
            r => { outcome.OnNext((r, null)); outcome.OnCompleted(); },
            ex => { outcome.OnNext((null, ex)); outcome.OnCompleted(); });

        var result = await outcome.Should().Within(TestTimeouts.WriteConvergence).Emit(
            "the delete must reach a terminal", cancellationToken: ct);
        result.Error.Should().BeNull(
            "a leaf whose activation was disposed mid-answer said nothing about whether it may be "
            + "deleted — the delete must re-ask the fresh activation and complete, never be refused "
            + "with the dead scope's ObjectDisposedException as a 'Validation error'");
        result.Removed.Should().BeTrue("the root was there, so this call really removed something");

        // POSITIVE CONTROL — the repro really happened: the hub was disposed and the genuine
        // Autofac fault was raised from its scope. Without it the delete would pass for the wrong
        // reason (the validator never fired, or the scope was still alive).
        validator.Fault.Should().NotBeNull(
            $"validation call #{call} of '{victimPath}' must have disposed that leaf's hub and "
            + "faulted from its dead scope — otherwise this test measured nothing");
        validator.Fault.Should().BeAssignableTo<ObjectDisposedException>(
            "the fault is the one production saw: a resolve from a disposed LifetimeScope");
        Output.WriteLine("repro fault: " + validator.Fault!.Message);
        validator.Calls(victimPath).Should().BeGreaterThan(call,
            "the leaf must have been asked AGAIN after its activation died — that re-ask on the "
            + "fresh activation is what the delete converges through");

        // THE STORE OF RECORD, not the report about it.
        var storage = mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        foreach (var p in new[] { rootPath, victimPath, siblingPath })
            (await storage.Exists(p).Should().Within(TestTimeouts.Quick).Emit(cancellationToken: ct))
                .Should().BeFalse($"'{p}' must be deleted — the recycle did not refuse the delete");
    }
}

/// <summary>
/// A deletion validator that, on its N-th call for ONE path, disposes that path's per-node hub,
/// waits for the disposal to COMPLETE, and then resolves from the hub's scope the way a
/// per-hub-scoped service does — raising the real Autofac <see cref="ObjectDisposedException"/>.
/// Every other call (and every other path) answers Valid at once.
///
/// <para>Instance state, never static: the mesh owns this singleton for the life of the test's
/// mesh.</para>
/// </summary>
internal sealed class DisposesItsLeafOnceValidator : INodeValidator
{
    private readonly ConcurrentDictionary<string, int> calls = new(StringComparer.OrdinalIgnoreCase);
    private volatile string? armedPath;
    private volatile Func<string, IMessageHub?>? hubOf;
    private int fireOnCall;
    private volatile Exception? fault;

    /// <summary>The fault the repro raised, or null when it never fired.</summary>
    public Exception? Fault => fault;

    /// <summary>How many times this validator was asked about <paramref name="path"/>.</summary>
    public int Calls(string path) => calls.TryGetValue(path, out var n) ? n : 0;

    /// <summary>Arms the repro for <paramref name="path"/> on its <paramref name="call"/>-th validation.</summary>
    public void Arm(string path, int call, Func<string, IMessageHub?> hubLookup)
    {
        fireOnCall = call;
        hubOf = hubLookup;
        armedPath = path;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } = [NodeOperation.Delete];

    /// <inheritdoc />
    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        var path = context.Node.Path;
        var n = calls.AddOrUpdate(path, 1, (_, c) => c + 1);
        if (armedPath is null
            || !string.Equals(path, armedPath, StringComparison.OrdinalIgnoreCase)
            || n != Volatile.Read(ref fireOnCall))
            return Observable.Return(NodeValidationResult.Valid());

        var hub = hubOf?.Invoke(path)
                  ?? throw new InvalidOperationException(
                      $"the repro needs '{path}' to have a live per-node hub while it is being validated");
        hub.Dispose();
        return hub.DisposalCompleted
            .Take(1)
            .DefaultIfEmpty()
            .Select(_ =>
            {
                try
                {
                    // What a per-hub-scoped service (INodeValidator itself, AccessService, …) does
                    // from its own body once its hub is gone.
                    hub.ServiceProvider.GetService(typeof(INodeValidator));
                }
                catch (ObjectDisposedException ex)
                {
                    fault = ex;
                    throw;
                }
                return NodeValidationResult.Valid();
            });
    }
}
