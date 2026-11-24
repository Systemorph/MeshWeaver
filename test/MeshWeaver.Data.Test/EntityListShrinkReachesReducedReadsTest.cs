using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Json;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// A subscriber's write that SHRINKS a list inside an entity must reach every reduced read of that
/// entity on the owner.
///
/// <para>Measured as the flake <c>GridRowSelectionFromViewsTest.ASecondTick_AddsToTheSelection</c>
/// (Plugins): unticking one of two rows writes the selection entity <c>keys: [a, d] → [d]</c>. The
/// differ expresses that as <c>remove /keys/1</c> + <c>replace /keys/0</c>, and the owner's
/// <c>PatchEntityStore</c> grouped the operations by their VERB and took a <c>remove</c> anywhere under
/// the entity to mean "remove the entity". The change item therefore carried TWO updates for one entity —
/// <c>[ENTITY REMOVED (null), entity = new value]</c> — and the entity reducer took the FIRST match, the
/// null, which the reduced stream's <c>Value: not null</c> filter then dropped. A reduced read that was
/// already open (any read created before the write landed — which load decides) never saw the new
/// value; one created after got a fresh snapshot and passed, which is why it only failed under load.</para>
///
/// <para>Deterministic: the patch is the one the differ produces, applied through the owner's own patch
/// function, and the reduction is the one every open reduced read runs — no timing involved.</para>
/// </summary>
public class EntityListShrinkReachesReducedReadsTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>An entity whose value is a list, as a row-selection state is.</summary>
    public record ListHolder(string Id, ImmutableList<string> Keys);

    private const string Instance = "selection";

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(source => source
                .WithType<ListHolder>(type => type
                    .WithKey(holder => holder.Id)
                    .WithInitialData([new ListHolder(Instance, ["a", "d"])]))));

    [HubFact]
    public async Task AShrunkList_IsOneUpdateOfTheEntity_AndReducesToTheNewValue()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var collection = workspace.DataContext.GetTypeSource(typeof(ListHolder))!.CollectionName;
        var stream = workspace.GetStream(new CollectionsReference(collection))!;
        var options = host.JsonSerializerOptions;
        var current = (await stream.Should().Within(TestTimeouts.Convergence).Emit("the owner's data is loaded")).Value!;

        var updated = WorkspaceOperations.Update(current, collection,
            instances => instances.Update(Instance, new ListHolder(Instance, ["d"])));
        // The patch the differ writes for [a, d] -> [d]: a remove FIRST, then the replace.
        var patch = JsonSerializer.Deserialize<JsonPatch>(
            $$"""
            [
              { "op": "remove",  "path": "/{{collection}}/\"{{Instance}}\"/keys/1" },
              { "op": "replace", "path": "/{{collection}}/\"{{Instance}}\"/keys/0", "value": "d" }
            ]
            """, options)!;

        var change = stream.ReduceManager.PatchFunction!(
            stream, current, JsonSerializer.SerializeToElement(updated, options), patch, "writer");

        change.Updates.Should().ContainSingle("a list edit inside an entity is ONE update of that entity")
            .Which.Value.Should().NotBeNull("the entity was edited, never removed");

        // What every reduced read of the entity runs on the frame.
        var reduced = (ChangeItem<object>)stream.ReduceManager
            .Reduce(change, new EntityReference(collection, Instance), initial: false)!;
        reduced.Value.Should().BeOfType<ListHolder>("a read that was already open must be handed the new value")
            .Which.Keys.Should().Equal("d");
    }
}
