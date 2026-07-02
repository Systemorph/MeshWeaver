using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MeshWeaver.Connection.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Test")]
// MeshNodeStreamCache idle-release tests observe the internal eviction seam
// (ReadStreamEvictions / IsReadStreamLive) to wait deterministically instead of polling.
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith.Test")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.PostgreSql.Test")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Sqlite.Test")]
