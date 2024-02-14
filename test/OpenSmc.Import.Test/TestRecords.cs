using System.ComponentModel.DataAnnotations;
using OpenSmc.Domain.Abstractions.Attributes;

namespace OpenSmc.Import.Test
{
    public record MyRecord
    {
        [Key]
        public string SystemName { get; init; }
        public string DisplayName { get; init; }

        public int Number { get; init; }

        public List<string> StringsList { get; init; }
        public string[] StringsArray { get; init; }

        public int[] IntArray { get; init; }
        public List<int> IntList { get; init; }
    }

    public record MyRecord2 : MyRecord
    {
        public DateTime Date { get; set; }
    }

    public record MyRecord3 : MyRecord;

    public record NotInheritedRecord
    {
        public string SystemName { get; init; }
        public string DisplayName { get; init; }
    }

    public record RecordWithAttribute
    {
        [MapTo("SystemName")]
        public string Name1 { get; init; }
        [MapTo("DisplayName")]
        public string Name2 { get; init; }
    }
}
