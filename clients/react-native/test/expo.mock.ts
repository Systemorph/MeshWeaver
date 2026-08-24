// `expo` stand-in for headless tests.
//
// src/rnContainers.tsx imports `expo` for its SIDE EFFECT: on web it is what installs
// `globalThis.expo`, without which expo-video's web build throws while it is still being evaluated
// (see the note at that import). Pulling the real package into vitest drags in the whole Expo
// runtime — expo/src/async-require/setup.ts reads `__DEV__`, which only the Metro transform defines
// — so 10 of 16 suites died on `ReferenceError: __DEV__ is not defined`.
//
// This stub does the one thing the import is there for, so the reason it exists stays visible in
// the test tree rather than being aliased away to nothing: it installs the same global, with a
// SharedObject base an unmocked expo-video/expo-audio web build could actually extend.
class SharedObject {
  private readonly listeners: Record<string, ((payload: unknown) => void)[]> = {};
  addListener(name: string, fn: (payload: any) => void) {
    (this.listeners[name] ??= []).push(fn);
    return { remove: () => { this.listeners[name] = (this.listeners[name] ?? []).filter((f) => f !== fn); } };
  }
  removeAllListeners(name: string) { delete this.listeners[name]; }
  emit(name: string, payload: unknown) { for (const fn of this.listeners[name] ?? []) fn(payload); }
  release() {}
}

(globalThis as any).expo ??= { SharedObject, SharedRef: SharedObject, EventEmitter: SharedObject, modules: {} };

export { SharedObject };
export const EventEmitter = SharedObject;
export const SharedRef = SharedObject;
