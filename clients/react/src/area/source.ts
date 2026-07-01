import type { AreaSource, AreaTree, MeshEvent } from "./types.js";
import { mergePatch, setPointer } from "./pointer.js";

/**
 * In-memory area source — drives the demo (and tests) from a literal area tree. `emit` records
 * events; an "update" optimistically writes the value at its pointer so edits reflect immediately,
 * exactly as the live stream would echo the merge-patch back.
 */
export class StaticAreaSource implements AreaSource {
  private state: AreaTree;
  private readonly listeners = new Set<() => void>();
  public readonly events: MeshEvent[] = [];

  constructor(initial: AreaTree) {
    this.state = initial;
  }

  getState = (): AreaTree => this.state;

  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  emit = (event: MeshEvent): void => {
    this.events.push(event);
    if (event.kind === "update" && event.pointer) {
      this.state = setPointer(this.state, event.pointer, event.value);
      this.notify();
    }
  };

  /** Apply an RFC 7396 merge-patch to the whole tree (what a live stream delivers). */
  applyPatch(patch: AreaTree): void {
    this.state = mergePatch(this.state, patch);
    this.notify();
  }

  private notify(): void {
    this.listeners.forEach((l) => l());
  }
}
