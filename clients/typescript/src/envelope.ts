// IMessageDelivery envelope (de)serialization — the ONE place that builds/parses the mesh's
// System.Text.Json delivery JSON (protobuf only frames it; see mesh.proto). Mirrors the Python
// SDK's envelope.py. Parsing is resilient to property casing; building must match the server's
// `JsonSerializer.Deserialize<IMessageDelivery>` — confirm `$type` + casing against a sample
// captured from the C# round-trip test.

export const DELIVERY_TYPE = "MessageDelivery"; // $type for the concrete delivery envelope
export const TYPE_KEY = "$type";
export const REQUEST_ID = "RequestId";

export interface Delivery {
  id?: string;
  sender?: string;
  target?: string;
  requestId?: string;
  messageType?: string;
  message: Record<string, unknown>;
  raw: Record<string, unknown>;
}

export function buildDeliver(opts: {
  deliveryId: string;
  sender: string;
  target: string;
  messageType: string;
  message: Record<string, unknown>;
}): string {
  return JSON.stringify({
    [TYPE_KEY]: DELIVERY_TYPE,
    id: opts.deliveryId,
    sender: opts.sender,
    target: opts.target,
    message: { [TYPE_KEY]: opts.messageType, ...opts.message },
    properties: {},
  });
}

export function parseDelivery(payload: string): Delivery {
  const root = JSON.parse(payload) as Record<string, unknown>;
  const props = (get(root, "properties") as Record<string, unknown>) ?? {};
  const message = (get(root, "message") as Record<string, unknown>) ?? {};
  return {
    id: get(root, "id") as string | undefined,
    sender: get(root, "sender") as string | undefined,
    target: get(root, "target") as string | undefined,
    requestId: get(props, REQUEST_ID) as string | undefined,
    messageType: typeof message === "object" ? (message[TYPE_KEY] as string | undefined) : undefined,
    message: typeof message === "object" ? message : {},
    raw: root,
  };
}

// Case-insensitive single-key lookup (camelCase vs PascalCase resilience).
function get(obj: unknown, key: string): unknown {
  if (!obj || typeof obj !== "object") return undefined;
  const rec = obj as Record<string, unknown>;
  if (key in rec) return rec[key];
  const low = key.toLowerCase();
  for (const k of Object.keys(rec)) if (k.toLowerCase() === low) return rec[k];
  return undefined;
}
