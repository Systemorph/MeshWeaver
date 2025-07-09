using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

namespace MeshWeaver.Data.TestDomain;

public record MyRecord
{
    [Key]
    public string SystemName { get; init; } = null!;
    public string DisplayName { get; init; } = null!;

    public int Number { get; init; }

    public List<string> StringsList { get; init; } = null!;
    public string[] StringsArray { get; init; } = null!;

    public int[] IntArray { get; init; } = null!;
    public List<int> IntList { get; init; } = null!;
}

public record MyRecord2 : MyRecord
{
    public DateTime Date { get; set; }
}

public record MyRecord3 : MyRecord;

public record NotInheritedRecord
{
    public string SystemName { get; init; } = null!;
    public string DisplayName { get; init; } = null!;
}

public record RecordWithAttribute
{
    [MapTo("SystemName")]
    public string Name1 { get; init; } = null!;

    [MapTo("DisplayName")]
    public string Name2 { get; init; } = null!;
}
