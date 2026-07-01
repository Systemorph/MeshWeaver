// Minimal mesh types the SDK surfaces in-language. Mirrors @meshweaver/client's types.ts and the
// Python SDK's types.py — one shape across every client.

export interface MeshNode {
  path?: string;
  name?: string;
  nodeType?: string;
  content: Record<string, unknown>;
  raw: Record<string, unknown>;
}

export function meshNodeFromChange(change: Record<string, unknown>): MeshNode {
  const g = (...keys: string[]): unknown => {
    for (const k of keys) if (k in change) return change[k];
    return undefined;
  };
  return {
    path: g("path", "Path") as string | undefined,
    name: g("name", "Name") as string | undefined,
    nodeType: g("nodeType", "NodeType") as string | undefined,
    content: (g("content", "Content") as Record<string, unknown>) ?? {},
    raw: change,
  };
}
