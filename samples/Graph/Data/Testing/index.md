# Testing — the platform's xunit estate, in-mesh

Every `Testing/<Suite>` NodeType is a former `test/<Project>.Test` xunit project whose accepted files run as `[MeshFact]` cases in the samples gate's mesh (`MeshWeaver.Testing.InMesh`, #4184). The refused files are named in each type's description and stay on xunit until their facility lands.
