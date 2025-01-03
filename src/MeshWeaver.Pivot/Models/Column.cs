﻿using System.Collections.Immutable;
using MeshWeaver.Pivot.Models.Interfaces;

namespace MeshWeaver.Pivot.Models
{
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
