using OpenSmc.Messaging;

namespace OpenSmc.Import;

public record ImportRequest(
    string FileName,
    string FileType,
    string Format,
    object TargetDataSource,
    bool SnapshotModeEnabled) : IRequest<DataChanged>;

