// Test stub for `next/headers` — the async request accessors an RSC reads. Next only provides
// them inside its server runtime, so without this the page's server component (AreaSnapshot) is
// untestable and the ONE thing that matters about it — how many round-trips a render makes, and
// with which credential — can only be inspected by reading the source.
//
// `setRequest` installs the request a test wants the component to see; `resetRequest` clears it.

let currentCookies: { name: string; value: string }[] = [];
let currentHeaders = new Headers();

export function setRequest(init: { cookies?: Record<string, string>; headers?: Record<string, string> }): void {
  currentCookies = Object.entries(init.cookies ?? {}).map(([name, value]) => ({ name, value }));
  currentHeaders = new Headers(init.headers ?? {});
}

export function resetRequest(): void {
  currentCookies = [];
  currentHeaders = new Headers();
}

export async function cookies(): Promise<{ getAll: () => { name: string; value: string }[] }> {
  return { getAll: () => currentCookies };
}

export async function headers(): Promise<Headers> {
  return currentHeaders;
}
