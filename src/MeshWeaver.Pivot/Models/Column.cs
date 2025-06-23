using System.Collections.Immutable;
using System.Text.Json.Serialization;
using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Pivot.Models
{
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
    [JsonDerivedType(typeof(Column), "MeshWeaver.Pivot.Models.Column")]
    [JsonDerivedType(typeof(ColumnGroup), "MeshWeaver.Pivot.Models.ColumnGroup")]
    public record Column : ItemWithCoordinates
    {
        public Column()
        {
            GrouperName = PivotConst.ColumnGrouperName;
        }

        public Column(string id, string displayName)
            : this()
        {
            Id = id;
            DisplayName = displayName;
            Coordinates = ImmutableList<object>.Empty.Add(id);
        }
    }
}
