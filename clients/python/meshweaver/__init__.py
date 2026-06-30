"""MeshWeaver Python client.

Connect a Python process to the mesh over gRPC and use mesh features natively — the foreign-language
counterpart of the MAUI/SignalR participant. Bring data in, do the work with native Python (pandas,
numpy, ...), write results back.

    import asyncio
    import meshweaver as mw

    async def main():
        mesh = await mw.Mesh.connect("https://atioz.meshweaver.cloud", token="...")
        stories = await mesh.search("nodeType:Story namespace:ACME")
        for s in stories:
            ...                                  # native Python does the work
        await mesh.patch("ACME/Stories/42", {"content": {"processed": True}})
        await mesh.close()

    asyncio.run(main())

Run ``scripts/gen_proto.sh`` once to generate the gRPC stubs from the canonical ``mesh.proto``.
"""
from .connection import MeshConnection, connect
from .mesh import Mesh
from .types import MeshNode
from .worker import CodeWorker, ExecResult, execute_python, serve

__all__ = ["Mesh", "MeshConnection", "MeshNode", "connect", "CodeWorker", "ExecResult", "execute_python", "serve"]
__version__ = "0.1.0"
