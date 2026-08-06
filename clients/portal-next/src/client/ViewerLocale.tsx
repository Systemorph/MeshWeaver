"use client";

// The viewer's language, read off their own User node — the client twin of AccessContext.Locale.
//
// Blazor resolves every string through AccessService.Localize, which reads AccessContext.Locale
// (populated from User.Locale). The browser is NOT the authority: a user who set German in their
// profile must get German on a machine whose navigator.language is English. So the profile wins,
// and navigator.language is only the pre-login / no-preference fallback — the same precedence the
// server applies.

import { useEffect, useState, type ReactNode } from "react";
import { LocaleProvider } from "@meshweaver/react";
import { useLiveConnection } from "./LiveConnection";

/** The browser's preferred language — the fallback before a profile is known. */
function browserLocale(): string | null {
  if (typeof navigator === "undefined") return null;
  return navigator.language ?? null;
}

export function ViewerLocaleProvider({ children }: { children: ReactNode }) {
  const live = useLiveConnection();
  const mesh = live.state.kind === "live" ? live.state.mesh : null;
  const [profileLocale, setProfileLocale] = useState<string | null>(null);

  useEffect(() => {
    if (!mesh?.userId) return;
    let alive = true;
    mesh
      .getNode(mesh.userId)
      .then((node) => {
        if (!alive || !node) return;
        const content = (node.content ?? {}) as Record<string, unknown>;
        const locale = content.locale ?? (node as Record<string, unknown>).locale;
        if (typeof locale === "string" && locale) setProfileLocale(locale);
      })
      .catch(() => undefined); // no profile read → stay on the browser fallback
    return () => {
      alive = false;
    };
  }, [mesh]);

  return <LocaleProvider locale={profileLocale ?? browserLocale()}>{children}</LocaleProvider>;
}
