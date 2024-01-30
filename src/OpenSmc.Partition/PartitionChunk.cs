using System;
using System.Collections.Generic;

namespace OpenSmc.Partition;

public record PartitionChunk(string Name, object PartitionId, IEnumerable<object> Items, Type Type);