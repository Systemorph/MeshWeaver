using System.Runtime.CompilerServices;

// The NodeType compile toolchain moved to MeshWeaver.Compiler (#1707). The moved public types
// keep their MeshWeaver.Graph.Configuration namespace and are forwarded here so BINARY consumers
// compiled against earlier releases — installed modules pin their compile-time assembly
// references — keep resolving them through MeshWeaver.Graph.
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.CodeQueryResolver))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.CodeQueryGroup))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.CompilationException))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.FrameworkBuildIdentity))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.FileSystemAssemblyStore))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.AssemblyStoreExtensions))]
[assembly: TypeForwardedTo(typeof(MeshWeaver.Graph.Configuration.SourceDiscoveryUnavailableException))]
