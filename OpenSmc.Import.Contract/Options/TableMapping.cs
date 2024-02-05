namespace OpenSmc.Import.Contract.Options;

public record TableMapping(IRowMapping RowMapping, bool SnapshotModeEnabled, string TableName);