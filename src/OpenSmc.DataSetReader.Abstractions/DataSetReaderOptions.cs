namespace OpenSmc.DataSetReader.Abstractions
{
    public record DataSetReaderOptions(char Delimiter, in bool WithHeaderRow, Type TypeToRestoreHeadersFrom, string ContentType);
}