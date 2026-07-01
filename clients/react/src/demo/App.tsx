import { useMemo, useState } from "react";
import { MeshAreaView } from "../index.js";
import { StaticAreaSource } from "../area/source.js";
import type { MeshEvent } from "../area/types.js";
import { sampleArea } from "./sample.js";

export default function App() {
  const [events, setEvents] = useState<MeshEvent[]>([]);
  const source = useMemo(() => {
    const s = new StaticAreaSource(sampleArea);
    const orig = s.emit;
    s.emit = (ev: MeshEvent) => {
      orig(ev);
      setEvents((prev) => [...prev.slice(-9), ev]);
    };
    return s;
  }, []);

  return (
    <div style={{ fontFamily: "Segoe UI, system-ui, sans-serif" }}>
      <MeshAreaView source={source} rootArea="main" />
      <pre
        style={{
          position: "fixed",
          bottom: 0,
          left: 0,
          right: 0,
          maxHeight: 120,
          overflow: "auto",
          margin: 0,
          padding: 8,
          background: "#1115",
          color: "#0a0",
          fontSize: 11,
        }}
      >
        {events.length === 0 ? "// click a button or edit a field — events appear here" : events.map((e, i) => `${i}: ${JSON.stringify(e)}`).join("\n")}
      </pre>
    </div>
  );
}
