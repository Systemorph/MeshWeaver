// IMessageDelivery envelope (de)serialization — the browser/React-Native twin of the Node SDK's
// envelope.ts (clients/typescript). protobuf only frames the bytes (see mesh.proto); THIS builds and
// parses the mesh's System.Text.Json delivery JSON. No Node imports — runs in a browser and in Hermes.
// Building must match the server's `JsonSerializer.Deserialize<IMessageDelivery>`; parsing is resilient
// to property casing. Confirm `$type` + casing against a sample captured from the C# round-trip test.

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

/** RFC-4122 id; uses crypto.randomUUID where present (browser), falls back for Hermes/older RN. */
export function newId(): string {
  const g = globalThis as { crypto?: { randomUUID?: () => string } };
  const uuid = g.crypto?.randomUUID?.();
  if (uuid) return uuid.replace(/-/g, "");
  return Math.random().toString(36).slice(2) + Math.random().toString(36).slice(2);
}
