// <meshweaver>
// Id: Testing/Graph/TestCollections
// DisplayName: Testing/Graph/TestCollections — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Collection definition for tests that use samples/Graph/Data.
/// These tests share file system resources and compilation cache,
/// so they must not run in parallel with each other.
/// </summary>
public class SamplesGraphDataCollection;
