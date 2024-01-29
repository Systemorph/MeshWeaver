namespace OpenSmc.DomainDesigner.ExcelParser
{
    public record PropertyDescriptor
    {
        public Type PropType { get; init; }

        public string BasicPropName { get; init; }

        public string ParsedPropName { get; init; }
        
        public string CellRef { get; init; }

        public bool IsList { get; init; }

        public bool Excluded { get; init; }
    }
}
