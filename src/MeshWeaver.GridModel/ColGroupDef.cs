using System.Collections.Immutable;

namespace MeshWeaver.GridModel
{
    public record ColGroupDef : ColDef
    {
        // ReSharper disable once EmptyConstructor
        public ColGroupDef() { }

        public IImmutableList<ColDef> Children { get; init; } = ImmutableList<ColDef>.Empty;

        public object GroupId { get; init; }

        // Set to true to keep columns in this group beside each other in the grid. Moving the columns outside of the group (and hence breaking the group) is not allowed.
        public bool? MarryChildren { get; init; }

        // Set to true if this group should be opened by default.
        public bool? OpenByDefault { get; init; }
    }
}
