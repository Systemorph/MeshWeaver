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
// RoutingGrainTurnIsolationTest (issue #1028) decorates the real PathResolutionService with one
// whose Subscribe BLOCKS, to prove a stalled route leg can no longer wedge the silo's routing.
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Orleans.Test")]
