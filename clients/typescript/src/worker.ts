// The node kernel — the Node/Bun counterpart of clients/python's `meshweaver.worker`. A Code node
// whose Language is `javascript` or `typescript` has no in-process runtime on the mesh, so the .NET
// kernel routes its SubmitCodeRequest to a connected `node/*` worker (CodeNodeType.ResolveKernelAddress
// → node/node-kernel). This module executes the snippet and writes the result to the SAME Activity
// node subscribers already watch — so js/ts output surfaces identically to C# and python.
//
//   npx @meshweaver/node-worker --url https://memex.meshweaver.cloud --token mw_... --address node/node-kernel
//   # co-deployed gate (trusted loopback, no token):
//   node dist/worker.js --url http://127.0.0.1:8082 --address node/node-kernel --reconnect
import * as vm from "node:vm";
import { MeshConnection, connect } from "./connection.js";
import type { Delivery } from "./envelope.js";

/** The stable address the .NET kernel routes javascript/typescript submissions to. */
export const DEFAULT_WORKER_ADDRESS = "node/node-kernel";

export interface ExecResult {
  stdout: string;
  returnValue: unknown; // best-effort JSON-able (the trailing expression's value)
  error: string | null; // the formatted throw, or null on success
}

function jsonable(value: unknown): unknown {
  if (value === undefined) return null;
  // Normalize to a plain JSON value — it crosses the wire as JSON, and this also detaches a vm-realm
  // object from the sandbox's prototypes. Non-serializable values (functions, cycles) stringify.
  try { return JSON.parse(JSON.stringify(value)); }
  catch { return String(value); }
}

function fmt(a: unknown): string {
  if (typeof a === "string") return a;
  try { return JSON.stringify(a); } catch { return String(a); }
}

/** Strip TypeScript types → runnable JavaScript with the bundled compiler (lazy — plain-JS runs pay nothing). */
async function transpileTs(code: string): Promise<string> {
  const ts = (await import("typescript")).default;
  return ts.transpileModule(code, {
    compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.None },
  }).outputText;
}

/**
 * Execute a js/ts snippet in a fresh sandbox — the testable core (no mesh). REPL semantics matching
 * the python worker: `console.log(...)` is captured as output, a trailing bare expression is the
 * return value (vm's completion value), `Inputs` exposes caller parameters. Any throw is caught and
 * formatted — a worker never wedges on a bad snippet.
 */
export async function executeCode(
  code: string, language = "javascript", inputs: Record<string, unknown> = {},
): Promise<ExecResult> {
  const out: string[] = [];
  const log = (...a: unknown[]) => { out.push(a.map(fmt).join(" ")); };
  const sandbox: Record<string, unknown> = {
    Inputs: inputs,
    console: { log, info: log, warn: log, error: log, debug: log },
  };
  let returnValue: unknown = null;
  let error: string | null = null;
  try {
    const js = language.toLowerCase() === "typescript" ? await transpileTs(code) : code;
    returnValue = vm.runInNewContext(js, vm.createContext(sandbox), { timeout: 30_000, displayErrors: true });
  } catch (e) {
    error = e instanceof Error ? (e.stack ?? e.message) : String(e);
  }
  return { stdout: out.join("\n"), returnValue: jsonable(returnValue), error };
}

/** Serves SubmitCodeRequest deliveries: execute the js/ts and patch the run's Activity node. */
export class CodeWorker {
  constructor(private readonly conn: MeshConnection) {
    conn.serve((d) => this.handle(d));
  }

  private async handle(delivery: Delivery): Promise<void> {
    if (delivery.messageType !== "SubmitCodeRequest") return; // not ours — ignore quietly
    const msg = delivery.message;
    const code = (msg.code ?? msg.Code ?? "") as string;
    const language = (msg.language ?? msg.Language ?? "javascript") as string;
    const activityPath = (msg.activityLogPath ?? msg.ActivityLogPath) as string | undefined;
    const inputs = (msg.inputs ?? msg.Inputs ?? {}) as Record<string, unknown>;

    const result = await executeCode(code, language, inputs);
    const status = result.error === null ? "Succeeded" : "Failed";
    const messages = [result.stdout, result.error].filter((m): m is string => !!m).map((m) => ({ message: m }));

    // Patch the Activity node (RFC 7396 merge under `patch`), under the requester's identity so a
    // trusted worker writes AS the user whose Code node this run is — like the in-process C# kernel.
    if (activityPath) {
      this.conn.post(activityPath, "PatchDataRequest", {
        reference: { $type: "MeshNodeReference" },
        patch: { content: { status, messages, returnValue: result.returnValue } },
      }, delivery.accessContext);
    }
    // Reply for request/response callers (the .NET dispatch awaiting a result).
    this.conn.respond(delivery, "SubmitCodeResponse", {
      status, returnValue: result.returnValue, output: result.stdout, error: result.error,
    });
  }
}

export interface ServeOptions {
  token?: string;
  address?: string;
  /** Gate mode: on a failed/dropped connection, wait and reconnect instead of exiting (outlives portal restarts). */
  reconnect?: boolean;
  retrySeconds?: number;
}

/** Connect + serve as the node kernel until SIGINT/SIGTERM, with an optional reconnect loop. */
export async function serve(url: string, opts: ServeOptions = {}): Promise<void> {
  const address = opts.address ?? DEFAULT_WORKER_ADDRESS;
  const retryMs = (opts.retrySeconds ?? 3) * 1000;
  let stopping = false;
  const stop = new Promise<"stop">((resolve) => {
    const done = () => { stopping = true; resolve("stop"); };
    process.once("SIGINT", done);
    process.once("SIGTERM", done);
  });

  while (!stopping) {
    let conn: MeshConnection | null = null;
    try {
      conn = await connect(url, { token: opts.token, address });
      new CodeWorker(conn);
      console.log(`node kernel connected as ${address} → ${url}`);
      const reason = await Promise.race([stop, conn.waitClosed().then(() => "closed" as const)]);
      conn.close();
      if (reason === "stop" || !opts.reconnect) return;
      console.error(`node kernel connection closed; reconnecting in ${retryMs / 1000}s`);
    } catch (e) {
      conn?.close();
      if (!opts.reconnect) throw e;
      console.error(`node kernel connect failed (${String(e)}); reconnecting in ${retryMs / 1000}s`);
    }
    if (!stopping) await Promise.race([stop, new Promise((r) => setTimeout(r, retryMs))]);
  }
}

/** Self-contained smoke test: execute a JS and a TS snippet, verify the computed value + captured output. */
export async function runDemo(): Promise<boolean> {
  const js = await executeCode("console.log('hello from node'); 6 * 7", "javascript");
  const ts = await executeCode("const answer: number = 6 * 7; console.log(`ts computed ${answer}`); answer", "typescript");
  console.log(JSON.stringify({ js, ts }, null, 2));
  const ok = js.returnValue === 42 && ts.returnValue === 42
    && js.stdout.includes("hello from node") && ts.stdout.includes("ts computed 42");
  console.log(ok ? "demo self-check OK" : "demo self-check FAILED");
  return ok;
}

interface Args { url?: string; token?: string; address?: string; reconnect: boolean; demo: boolean; }
function parseArgs(argv: string[]): Args {
  const a: Args = { reconnect: false, demo: false };
  for (let i = 0; i < argv.length; i++) {
    switch (argv[i]) {
      case "--url": a.url = argv[++i]; break;
      case "--token": a.token = argv[++i]; break;
      case "--address": a.address = argv[++i]; break;
      case "--reconnect": a.reconnect = true; break;
      case "--demo": a.demo = true; break;
    }
  }
  return a;
}

export async function main(argv = process.argv.slice(2)): Promise<void> {
  const args = parseArgs(argv);
  if (args.demo) { process.exit((await runDemo()) ? 0 : 1); }
  if (!args.url) { console.error("usage: node-worker --url <portal> [--token mw_..] [--address node/node-kernel] [--reconnect] | --demo"); process.exit(2); }
  await serve(args.url, { token: args.token, address: args.address, reconnect: args.reconnect });
}

// Run when invoked directly (node dist/worker.js …), not when imported.
if (import.meta.url === `file://${process.argv[1]}`) {
  main().catch((e) => { console.error(e); process.exit(1); });
}
