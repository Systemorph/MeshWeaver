using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A recursive delete whose subtree enumeration gave NO ANSWER must refuse, never plan
/// "the root and nothing else" (MeshWeaver#7461).
///
/// <para>The store's <see cref="IStorageAdapter.ListDescendantPaths"/> for the deleted root completes
/// without emitting — a breach of "emits one complete snapshot and completes". Read as "no
/// descendants", the planner would delete the root alone and the drain verifier, enumerating through
/// the same silent store, would report the subtree drained over a child that is still stored. The
/// delete must FAIL, name the missing answer, and leave the child in storage.</para>
///
/// <para>The positive control is the same mesh with a store that ANSWERS — an empty collection for
/// a root that genuinely has no children. That delete must succeed: a fix that refused every empty
/// enumeration would pass the first test and break every leaf delete.</para>
/// </summary>
public class AnUnansweredSubtreeEnumerationRefusesTheDeleteTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly LatentDeleteStorageAdapter _storage = new(new InMemoryStorageAdapter());

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.ConfigureServices(services =>
        {
            // Registered BEFORE the bootstrap's TryAddSingleton, so this IS the store of record and
            // the production decorator chain wraps it.
            services.AddSingleton<IStorageAdapter>(_storage);
            return services;
        }));

    [Fact]
    public async Task ADeleteWhoseEnumerationCompletesEmpty_FailsAndLeavesTheChildStored()
    {
        var rootPath = $"{TestPartition}/unanswered";
        var childPath = $"{rootPath}/child";
        await NodeFactory.CreateNode(
                new MeshNode("unanswered", TestPartition) { Name = "unanswered", NodeType = "Markdown" })
            .Should().Within(30.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);
        await NodeFactory.CreateNode(
                new MeshNode("child", rootPath) { Name = "child", NodeType = "Markdown" })
            .Should().Within(30.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);

        _storage.UnansweredEnumerationRoot = rootPath;

        // Producer -> test signal: the delete's ERROR arm completes the subject; a success emission
        // leaves it empty and times the wait out, which is exactly the failure this test reports.
        var failure = new AsyncSubject<Exception>();
        using var deleting = NodeFactory.DeleteNode(rootPath).Subscribe(
            _ => { },
            ex =>
            {
                failure.OnNext(ex);
                failure.OnCompleted();
            });

        var reported = await failure.Should().Within(60.Seconds()).Emit(
            "a delete whose subtree enumeration never answered must FAIL — never plan the root alone");

        _storage.UnansweredEnumerations.Should().BeGreaterThan(0,
            "the test only discriminates if the enumeration really went unanswered");
        reported.ToString().Should().Contain("completed without an answer",
            "the caller must be told the subtree is UNKNOWN, not handed a generic failure");
        (await _storage.Inner.Exists(childPath).Should().Within(10.Seconds()).Emit())
            .Should().BeTrue("nothing under a root nobody enumerated may be touched");
    }

    [Fact]
    public async Task ADeleteWhoseEnumerationAnswersEmpty_Succeeds()
    {
        var rootPath = $"{TestPartition}/answered-empty";
        await NodeFactory.CreateNode(
                new MeshNode("answered-empty", TestPartition) { Name = "answered-empty", NodeType = "Markdown" })
            .Should().Within(30.Seconds()).Emit(cancellationToken: TestContext.Current.CancellationToken);

        (await NodeFactory.DeleteNode(rootPath).Should().Within(60.Seconds()).Emit(
                "a root with genuinely no children answers with an empty enumeration, which IS an answer"))
            .Should().BeTrue();
        (await _storage.Inner.Exists(rootPath).Should().Within(10.Seconds()).Emit())
            .Should().BeFalse("the root must be gone");
    }
}
