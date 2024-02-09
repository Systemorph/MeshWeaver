namespace OpenSmc.Import.Options;

public record TableMapping(IRowMapping RowMapping, bool SnapshotModeEnabled, string TableName);