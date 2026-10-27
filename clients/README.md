# MeshWeaver clients

Foreign-language clients that **join the mesh** over gRPC — the same participant role the portal plays
over SignalR.

Full architecture: `src/MeshWeaver.Documentation/Data/Architecture/ForeignLanguageIntegration.md`
(transport deep-dive: `ForeignLanguageBridge.md`).

## Packages

| Path | Package | What it is |
|---|---|---|
| [`python/`](python/) | `meshweaver` | Python SDK — `grpc.aio` transport + mesh operations, and the Python worker |

**Why the Python SDK stays in the platform repository.** Two images this repository builds COPY it —
`deploy/python-gate` and `deploy/pandas-gate` — and it generates its stubs from the platform's one
canonical proto (`src/MeshWeaver.Mesh.Contract/Protos/mesh.proto`). Moving it would make a core image
depend on another repository.

**Everything else left.** The web, desktop and mobile clients (`typescript/`, `grpc-web/`, `react/`,
`react-native/`, `portal/`, `portal-next/`) and the voice gateway (`voice-gateway/`, an ESPHome voice
satellite wired to agent threads) live in MeshWeaver.Plugins `clients/`, beside the hosts and the Speech
module they talk to.

## CI

`.github/workflows/clients.yml` runs the `meshweaver` (Python) pytest on any change under `clients/` or to
the canonical proto.
