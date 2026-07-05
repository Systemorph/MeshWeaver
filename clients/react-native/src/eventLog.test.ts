// Event-log outbox parity: the SAME contract runs against BOTH the in-memory reference store and the
// SQLite store, so the RN offline outbox behaves identically to the C# IEventLogStore (idempotent
// append by (path,kind,version), seq-ordered readFrom with a limit, maxSeq, monotonic cursor).
//
// The SQLite store is exercised against a REAL engine via node:sqlite when the test runtime provides
// it (Node ≥ 22.3). CI runs vitest on Node 20 (no node:sqlite), where that block is SKIPPED — never
// failed: node:sqlite is only a stand-in for expo-sqlite (the RN runtime engine), and the SQLite
// path's SQL is additionally covered by the C# SqliteEventLogStore integration tests. It is detected
// SYNCHRONOUSLY via process.getBuiltinModule (a static `import "node:sqlite"` would throw at module
// load on Node 20 and fail the whole file instead of skipping).

import { describe, expect, it } from "vitest";
import {
  InMemoryEventLogStore,
  SqliteEventLogStore,
  type EventLogDb,
  type EventLogStore,
  type MeshChangeEvent,
} from "./eventLog.js";

type DatabaseSyncCtor = new (path: string) => {
  exec(sql: string): void;
  prepare(sql: string): {
    run(...params: unknown[]): { lastInsertRowid: number | bigint; changes: number | bigint };
    get(...params: unknown[]): unknown;
    all(...params: unknown[]): unknown[];
  };
};

let DatabaseSync: DatabaseSyncCtor | undefined;
try {
  // process.getBuiltinModule exists on Node ≥ 22.3; node:sqlite lands there too. Reached via globalThis
  // (no @types/node needed), optional-chained + try/caught so Node 20 (CI) yields undefined — the
  // SQLite parity block is then skipped, not failed.
  const proc = (globalThis as { process?: { getBuiltinModule?: (id: string) => { DatabaseSync?: DatabaseSyncCtor } } }).process;
  DatabaseSync = proc?.getBuiltinModule?.("node:sqlite")?.DatabaseSync;
} catch {
  /* runtime without node:sqlite — the SQLite parity block is skipped below */
}

/** An EventLogDb backed by an in-memory node:sqlite database — the test twin of an expo-sqlite handle. */
function nodeSqliteDb(ctor: DatabaseSyncCtor): EventLogDb {
  const db = new ctor(":memory:");
  return {
    async execAsync(sql) {
      db.exec(sql);
    },
    async runAsync(sql, params) {
      const r = db.prepare(sql).run(...params);
      return { lastInsertRowId: Number(r.lastInsertRowid), changes: Number(r.changes) };
    },
    async getFirstAsync<T>(sql: string, params: unknown[]) {
      return (db.prepare(sql).get(...params) as T | undefined) ?? null;
    },
    async getAllAsync<T>(sql: string, params: unknown[]) {
      return db.prepare(sql).all(...params) as T[];
    },
  };
}

const evt = (over: Partial<MeshChangeEvent> = {}): MeshChangeEvent => ({
  id: "n1",
  path: "conflict/doc",
  kind: "Updated",
  version: 1,
  timestamp: "2026-07-05T00:00:00.000Z",
  ...over,
});

/** The store contract, run identically against every EventLogStore implementation. */
function contractTests(make: () => EventLogStore) {
  it("append assigns increasing sequence numbers", async () => {
    const s = make();
    expect(await s.append(evt({ path: "a", version: 1 }))).toBeLessThan(
      await s.append(evt({ path: "b", version: 1 })),
    );
  });

  it("append is idempotent by (path, kind, version): same seq, no extra row", async () => {
    const s = make();
    const first = await s.append(evt());
    const again = await s.append(evt({ timestamp: "2026-07-05T12:00:00.000Z" })); // same key, later ts
    expect(again).toBe(first);
    expect((await s.readFrom(0)).length).toBe(1); // ONE row (assert count, not maxSeq — autoincrement may gap)
  });

  it("distinct (path|kind|version) are separate rows", async () => {
    const s = make();
    await s.append(evt({ version: 1 }));
    await s.append(evt({ version: 2 })); // different version
    await s.append(evt({ kind: "Deleted" })); // different kind
    await s.append(evt({ path: "conflict/other" })); // different path
    expect((await s.readFrom(0)).length).toBe(4);
  });

  it("readFrom returns entries after a seq, ordered, honouring the limit", async () => {
    const s = make();
    const seqs: number[] = [];
    for (let i = 0; i < 5; i++) seqs.push(await s.append(evt({ path: `p${i}`, version: 1 })));
    const after1 = await s.readFrom(seqs[0]);
    expect(after1.map((e) => e.seq)).toEqual(seqs.slice(1)); // strictly greater, in order
    expect((await s.readFrom(0, 2)).length).toBe(2); // limit respected
    expect(after1[0].event.path).toBe("p1"); // payload round-trips
  });

  it("maxSeq tracks the highest assigned seq (0 when empty)", async () => {
    const s = make();
    expect(await s.maxSeq()).toBe(0);
    await s.append(evt({ path: "x" }));
    const last = await s.append(evt({ path: "y" }));
    expect(await s.maxSeq()).toBe(last);
  });

  it("cursor is 0 by default and advances monotonically", async () => {
    const s = make();
    expect(await s.getCursor("scheduled-actions")).toBe(0);
    await s.setCursor("scheduled-actions", 5);
    expect(await s.getCursor("scheduled-actions")).toBe(5);
    await s.setCursor("scheduled-actions", 3); // lower — must NOT move backward
    expect(await s.getCursor("scheduled-actions")).toBe(5);
    await s.setCursor("scheduled-actions", 9);
    expect(await s.getCursor("scheduled-actions")).toBe(9);
  });
}

describe("InMemoryEventLogStore", () => contractTests(() => new InMemoryEventLogStore()));

describe.skipIf(!DatabaseSync)("SqliteEventLogStore (real node:sqlite; skipped on Node < 22.3)", () =>
  contractTests(() => new SqliteEventLogStore(nodeSqliteDb(DatabaseSync!))),
);
