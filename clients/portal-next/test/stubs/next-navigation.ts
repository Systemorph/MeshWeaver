// Vitest stub for `next/navigation`. The app-router hooks (useRouter, …) require Next's
// AppRouterContext, which a bare renderToString / render in a unit test never provides — the real
// useRouter THROWS ("expected app router to be mounted") without it. This stub returns a no-op router
// so components that call useRouter (LiveArea's access-denied redirect) render in tests; the REAL
// next/navigation is used at runtime (this alias lives only in vitest.config.ts, never next.config).
export function useRouter() {
  return {
    push: () => {},
    replace: () => {},
    refresh: () => {},
    back: () => {},
    forward: () => {},
    prefetch: () => {},
  };
}

export function usePathname(): string {
  return "/";
}

export function useSearchParams(): URLSearchParams {
  return new URLSearchParams();
}

export function redirect(): never {
  throw new Error("NEXT_REDIRECT");
}

export function notFound(): never {
  throw new Error("NEXT_NOT_FOUND");
}
