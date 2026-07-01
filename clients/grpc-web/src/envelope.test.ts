import { describe, expect, it } from "vitest";
import { buildDeliver, parseDelivery, newId, DELIVERY_TYPE, TYPE_KEY } from "./envelope";

describe("envelope", () => {
  it("builds an IMessageDelivery the server can deserialize ($type + nested message $type)", () => {
    const json = buildDeliver({
      deliveryId: "d1",
      sender: "node/abc",
      target: "mesh/main",
      messageType: "QueryRequest",
      message: { query: "nodeType:Story" },
    });
    const obj = JSON.parse(json);
    expect(obj[TYPE_KEY]).toBe(DELIVERY_TYPE);
    expect(obj.id).toBe("d1");
    expect(obj.sender).toBe("node/abc");
    expect(obj.target).toBe("mesh/main");
    expect(obj.message[TYPE_KEY]).toBe("QueryRequest"); // the inner message carries its own $type
    expect(obj.message.query).toBe("nodeType:Story");
    expect(obj.properties).toEqual({});
  });

  it("round-trips through parseDelivery", () => {
    const parsed = parseDelivery(
      buildDeliver({ deliveryId: "d2", sender: "s", target: "t", messageType: "Ping", message: { n: 1 } }),
    );
    expect(parsed.id).toBe("d2");
    expect(parsed.sender).toBe("s");
    expect(parsed.target).toBe("t");
    expect(parsed.messageType).toBe("Ping");
    expect(parsed.message.n).toBe(1);
  });

  it("extracts RequestId from properties (correlation key)", () => {
    const parsed = parseDelivery(
      JSON.stringify({ id: "resp1", message: { $type: "EchoResponse" }, properties: { RequestId: "req-42" } }),
    );
    expect(parsed.requestId).toBe("req-42");
  });

  it("parses case-insensitively (PascalCase server casing)", () => {
    const parsed = parseDelivery(
      JSON.stringify({ Id: "X", Sender: "p", Target: "q", Message: { $type: "M" }, Properties: { RequestId: "r" } }),
    );
    expect(parsed.id).toBe("X");
    expect(parsed.sender).toBe("p");
    expect(parsed.target).toBe("q");
    expect(parsed.requestId).toBe("r");
    expect(parsed.messageType).toBe("M");
  });

  it("newId is unique and dash-free", () => {
    const a = newId();
    const b = newId();
    expect(a).not.toBe(b);
    expect(a).not.toContain("-");
  });
});
