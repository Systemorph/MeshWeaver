using System.Runtime.CompilerServices;

// 🚨 TYPE FORWARDERS — do not delete, and do not "clean these up".
//
// These types moved OUT of MeshWeaver.Graph when the NodeType compile pipeline was separated from
// the graph model. Modules that were PUBLISHED BEFORE that split are already compiled against
// `MeshWeaver.Graph!<name>` and resolve it by assembly-qualified name at load time; without a
// forwarder each of them fails with TypeLoadException on the first portal that adopts the new
// platform. Rebuilding the consumer does not help — the point is the ones already shipped.
//
// A FORWARDER, never a shim: a forwarder keeps ONE type identity, so `is`/`as`, serialization and
// reference equality all keep working across the boundary. A re-declared "compatibility" type in
// the old assembly would mint a SECOND identity and reintroduce exactly the trap-door the gate in
// dotnet-test.yml exists to refuse.
//
// A forwarder cannot rename, which is the other half of why the moved types kept their original
// namespaces: the assembly moved, the namespace did not.

// ── moved to MeshWeaver.Compiler.Pipeline ─────────────────────────────────────────
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.AssemblyCacheClaim))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.AssemblyCacheGeneration))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.AssemblyCacheGenerations))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.AssemblyCacheRetention))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.AssemblyCacheSweepPlan))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.AssemblyCacheSweepResult))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.BakeState))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.CompilationCacheOptions))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.CompileThread))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.DispatchCompileTrigger))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.InstalledModulesFingerprint))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.NodeSources))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.NodeTypeAdoptionRegistry))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.NodeTypeBakeEntry))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.NodeTypeBakeReport))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.NodeTypeBakeStatus))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.NodeTypeCompileParkRegistry))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.NodeTypeCompileState))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.NodeTypeCompileStateMirror))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.NodeTypeDependencyGraph))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.NodeTypeParkedException))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.NodeTypeUnparkPostDeletionHandler))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.OverlayHealBudget))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.PrebuiltAssemblySeeder))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.PrebuiltRequiredException))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.RecompileClosure))]

// ── moved to MeshWeaver.Graph.Contract ─────────────────────────────────────────
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.BuildClaimRequest))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.BuildGo))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.BuildState))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.BuildStatus))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.CreateNodeTypeReleaseRequest))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.CreateNodeTypeReleaseResponse))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.NodeTypeDefinition))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.NodeTypeRelease))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.ReleaseArchitecture))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.ReleaseArtifact))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.ReleaseArtifactMatch))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.ReleaseArtifactResolver))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.ServedBuildIdentity))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.SyncedQueryDataSourceExtensions))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.SyncedQueryKey))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.SyncedQueryMeshNodes))]

