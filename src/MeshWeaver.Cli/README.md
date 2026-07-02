# MeshWeaver.Cli (`memex`)

Command-line interface for MeshWeaver / Memex. Operates a portal's mesh over the REST API — read, search, mutate, compile, and mirror mesh nodes from the shell or from scripts.

## Install

```bash
dotnet tool install -g MeshWeaver.Cli
```

## Log in

Create an API token in your portal (Profile → API Tokens), then:

```bash
memex login mw_yourtoken --base-url https://memex.meshweaver.cloud
```

The token and base URL are stored in `~/.memex/config.json`; `$MEMEX_TOKEN` / `$MEMEX_BASE_URL` or `--token` / `--base-url` override per call.

## Commands

| Command | Purpose |
|---|---|
| `get <path>` | Read a node or resource by path |
| `search <query>` | Search the mesh (GitHub-style query, e.g. `nodeType:Agent`) |
| `create -f node.json` | Create a node from a JSON file |
| `update -f nodes.json` | Full-replace update from a JSON array file |
| `patch <path> …` | Partial update of a node's top-level fields |
| `delete <paths…>` | Delete nodes (recursive) |
| `move <src> <dst>` / `copy <src> <ns>` | Move / copy a node and its descendants |
| `upload <path> <file>` | Upload a file into a node's content collection |
| `compile <path>` / `diagnostics <path>` | Compile a NodeType and inspect diagnostics |
| `execute-script <path>` | Run an executable Code node through the kernel |
| `mirror push\|pull <remoteUrl> <source>` | Mirror a subtree between two portals |
| `recycle <path>` | Force a fresh hub initialisation |
| `navigate-to <path>` / `base-url` | Print the browser URL for a path / the portal base URL |

All commands print the server's JSON verbatim, so output pipes cleanly into `jq`. Errors go to stderr with a non-zero exit code.

## Learn more

MeshWeaver source and documentation: https://github.com/Systemorph/MeshWeaver — or browse the live docs at https://memex.meshweaver.cloud.
