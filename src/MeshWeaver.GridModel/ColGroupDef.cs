#nullable enable
using System.Collections.Immutable;

namespace MeshWeaver.GridModel
{
    /// <summary>
    /// A column group (ag-Grid style <c>ColGroupDef</c>): a header that contains nested
    /// <see cref="ColDef"/> children. Inherits the column properties of <see cref="ColDef"/>.
    /// </summary>
    public record ColGroupDef() : ColDef
    {

        /// <summary>The columns (or nested groups) contained within this group.</summary>
        public IImmutableList<ColDef> Children { get; init; } = ImmutableList<ColDef>.Empty;

        /// <summary>Unique identifier for the group.</summary>
        public object? GroupId { get; init; }

        /// <summary>When <c>true</c>, keeps the group's columns adjacent; they cannot be moved out of the group.</summary>
        public bool? MarryChildren { get; init; }

        /// <summary>When <c>true</c>, the group is expanded by default.</summary>
        public bool? OpenByDefault { get; init; }
    }
}
