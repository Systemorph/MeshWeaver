using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>An entity that IS a workspace type source.</summary>
public record MappedEntity(string Id, string Text);

/// <summary>
/// Derives from a mapped entity but has NO collection of its own. The write path deliberately
/// resolves such a type to its BASE's type source (WorkspaceOperations.ClassifyForRouting stores a
/// derived instance into the base collection; ImportManager falls back to the base explicitly).
/// </summary>
public record UnmappedDerivedEntity(string Id, string Text, int Extra) : MappedEntity(Id, Text);

/// <summary>
/// <see cref="IWorkspace.GetStream{T}()"/> promises <c>null</c> for a type that is not a workspace
/// type source — a documented contract every caller codes to with <c>?? Observable.Return(...)</c>
/// (SpaceLayoutAreas.Overview, the Store plugin's Catalog area). Breaking it does not degrade
/// gracefully: the <see cref="System.ArgumentException"/> escapes the layout-area render and the
/// user gets "Rendering failed for area X" instead of the area.
/// </summary>
public class GetStreamUnknownTypeContractTest : HubTestBase
{
    public GetStreamUnknownTypeContractTest(ITestOutputHelper output) : base(output) { }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds.WithType<MappedEntity>()));

    /// <summary>
    /// 🚨 The guard and the resolution must consult the SAME lookup.
    ///
    /// The guard used <c>DataContext.GetTypeSource(Type)</c>, which walks the BASE-TYPE chain —
    /// an affordance the WRITE path needs. The very next line resolved the collection name through
    /// <c>TypeRegistry.TryGetCollectionName</c>, which does NOT walk. So a type whose base is a
    /// type source, but which owns no collection, passed the guard and then threw
    /// "Type UnmappedDerivedEntity is unknown." out of the caller's render.
    /// </summary>
    [Fact]
    public void DerivedTypeWithoutItsOwnCollection_ReturnsNull_InsteadOfThrowing()
    {
        var workspace = GetHost().GetWorkspace();

        // An ArgumentException here escapes the layout-area render as "Rendering failed for area X".
        var exception = Record.Exception(() => workspace.GetStream<UnmappedDerivedEntity>());
        Assert.Null(exception);

        // ...and the documented answer for an unmapped type is null, not an empty stream.
        Assert.Null(workspace.GetStream<UnmappedDerivedEntity>());
    }

    /// <summary>Scope guard: tightening the guard must not stop a genuinely mapped type resolving.</summary>
    [Fact]
    public void MappedType_StillResolves()
        => Assert.NotNull(GetHost().GetWorkspace().GetStream<MappedEntity>());
}
