namespace OpenSmc.DataSetReader.Abstractions
{
    public record DataSetReaderOptions
    {
        public char Delimiter { get; init; } = ',';
        public bool WithHeaderRow { get; init; } = true;
        public Type TypeToRestoreHeadersFrom { get; init; }
    }
}