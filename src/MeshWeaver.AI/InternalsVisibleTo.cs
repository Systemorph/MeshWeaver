using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MeshWeaver.Threading.Test")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Orleans.Test")]

// The AI menu entries are asserted against the PORTAL shell they integrate with, so that suite
// spans both halves and needs the seed list (#2276). A test project may see the module's internals;
// the platform may not reference it at all, which is the invariant that matters.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Memex.Portal.Shared.Test")]
