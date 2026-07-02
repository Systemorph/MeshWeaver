"use client";

// The header avatar menu — the React port of the Blazor shell's UserProfile
// (src/MeshWeaver.Blazor.Portal/Components/UserProfile.razor + .razor.cs):
//
//   - 24px initials avatar button; the menu shows "Logged in as:" + Sign out, then the clickable
//     52px persona (name + username) navigating to /User/{userId}.
//   - The frontend toggle lives here too — the mirror of Blazor's "Try the new frontend":
//     "Back to classic" sets the mw-frontend cookie and routes through GET /frontend/blazor.
//   - Unauthenticated sessions get a Login button (→ /login, the portal's unified login page).
//
// Initials: first letter of a single-word name, first+last initials otherwise
// (UserProfile.GetInitials). The display name comes from the user's own node (the same
// name OnboardingMiddleware stamps into the Blazor AccessContext).

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { Avatar, Button, Menu, MenuList, MenuPopover, MenuTrigger, Text } from "@fluentui/react-components";
import { ArrowHookUpLeft20Regular } from "@fluentui/react-icons";
import { useLiveConnection } from "./LiveConnection";

/** UserProfile.GetInitials — first letter, or first+last word initials. */
export function initialsOf(name: string): string {
  const s = name.trim();
  if (!s) return "";
  const lastSpace = s.lastIndexOf(" ");
  if (lastSpace < 0) return s[0].toUpperCase();
  return `${s[0].toUpperCase()}${s[lastSpace + 1].toUpperCase()}`;
}

function switchToClassic() {
  // Same toggle the Vite SPA carries: the portal's GET /frontend/blazor sets the mw-frontend
  // override cookie and redirects. Also set the cookie client-side so the choice sticks when
  // this app is served standalone (no portal endpoint on this origin).
  document.cookie = "mw-frontend=Blazor; path=/; max-age=31536000; samesite=lax";
  window.location.href = "/frontend/blazor";
}

async function signOut() {
  // The portal's logout endpoint depends on the auth mode: /auth/logout with external providers,
  // /dev/logout for dev-login (the same split AuthenticationNavigationService applies). Probe the
  // provider endpoint first; both clear the session cookie on the fetch response, then land on
  // the unified /login page.
  try {
    const resp = await fetch("/auth/logout", { method: "GET", credentials: "same-origin", redirect: "manual" });
    if (resp.status === 404) await fetch("/dev/logout", { method: "GET", credentials: "same-origin", redirect: "manual" });
  } catch {
    // network hiccup — still navigate; the login page will show the real session state
  }
  window.location.href = "/login";
}

export function UserProfileMenu() {
  const live = useLiveConnection();
  const router = useRouter();
  const mesh = live.state.kind === "live" ? live.state.mesh : null;
  const userId = mesh?.userId ?? "";

  const [name, setName] = useState("");
  useEffect(() => {
    if (!mesh || !userId) return;
    let liveFlag = true;
    // The user's display name lives on their own node — the same source the Blazor
    // AccessContext name is stamped from.
    mesh.getNode(userId).then((node) => {
      if (!liveFlag || !node) return;
      const n = typeof node.name === "string" ? node.name : "";
      setName(n || userId);
    });
    return () => {
      liveFlag = false;
    };
  }, [mesh, userId]);

  if (live.state.kind === "offline" || (live.state.kind === "live" && !userId)) {
    return (
      <Button appearance="transparent" onClick={() => (window.location.href = "/login")}>
        Login
      </Button>
    );
  }

  const displayName = name || userId || "…";

  return (
    <Menu positioning="below-end">
      <MenuTrigger disableButtonEnhancement>
        <Button
          appearance="transparent"
          aria-label="User profile"
          icon={<Avatar name={displayName} initials={initialsOf(displayName)} size={24} color="colorful" />}
        />
      </MenuTrigger>
      <MenuPopover style={{ minWidth: 280, padding: 12 }}>
        <MenuList>
          <div style={{ display: "flex", alignItems: "center", gap: 8, marginBottom: 8 }}>
            <Text size={200} style={{ flex: 1 }}>
              Logged in as:
            </Text>
            <Button appearance="subtle" size="small" onClick={() => void signOut()}>
              Sign out
            </Button>
          </div>
          <div
            onClick={() => userId && router.push(`/User/${userId}`)}
            style={{ display: "flex", alignItems: "center", gap: 12, cursor: "pointer", padding: "4px 0" }}
          >
            <Avatar name={displayName} initials={initialsOf(displayName)} size={48} color="colorful" />
            <div style={{ minWidth: 0 }}>
              <div style={{ fontWeight: 600 }}>{displayName}</div>
              <Text size={200} style={{ color: "var(--colorNeutralForeground3)" }}>
                {userId}
              </Text>
            </div>
          </div>
          <div style={{ marginTop: 8 }}>
            <Button appearance="subtle" size="small" icon={<ArrowHookUpLeft20Regular />} onClick={switchToClassic}>
              Back to classic
            </Button>
          </div>
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}
