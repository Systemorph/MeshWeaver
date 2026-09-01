using System.Runtime.CompilerServices;

// DocumentPaths moved to MeshWeaver.Mesh.Contract, keeping its
// MeshWeaver.ContentCollections.Indexing namespace, and this assembly forwards the name so nothing
// that binds it has to change — not source in this repo, not a module already published against
// this assembly.
//
// WHY THE FORWARDER COMES FIRST, AS ITS OWN CHANGE.
// The indexing pipeline is moving to MeshWeaver.Plugins, and Mesh.Operations stays here needing
// exactly this one type — so the type stays and the pipeline goes. That split is CIRCULAR if done
// in one step: the plugins copy of the pipeline needs DocumentPaths from Mesh.Contract (so it wants
// the platform change first), while the platform cannot delete the pipeline until plugins ships it
// (so it wants plugins first). Neither side can be green before the other.
//
// Landing the relocation PLUS this forwarder on its own breaks the cycle. Afterwards both trees
// compile at every point: this assembly still answers for the name, and Mesh.Contract carries the
// definition the plugins copy binds.
[assembly: TypeForwardedTo(typeof(MeshWeaver.ContentCollections.Indexing.DocumentPaths))]
