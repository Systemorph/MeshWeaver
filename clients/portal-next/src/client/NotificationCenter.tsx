"use client";

// Notification bell + panel — the React port of the Blazor shell's NotificationCenter
// (src/MeshWeaver.Blazor.Portal/Components/NotificationCenter.razor) and
// NotificationCenterPanel.razor. Behavior pinned against the Blazor source:
//
//   - Unread count off "nodeType:Notification sort:CreatedAt-desc" (RLS gates visibility to the
//     caller), badge capped at 99+; opening the panel clears the badge optimistically while the
//     panel persists IsRead durably (mark-all-read on the first populated load).
//   - Item: icon (custom or per-NotificationType default) · title · 2-line message · time · creator,
//     with an unread accent; click marks read and navigates to targetNodePath (or the main node).
//   - Second bell click (or overlay/dismiss) closes — the panel is a toggle, never stacked.
//
// The Blazor bell binds a LIVE IMeshService.Query subscription; synced queries are not exposed
// over the browser wire yet, so this port refreshes the same query on connect, on navigation,
// and on panel open — the same query, snapshot-refreshed at interaction points.

import { useCallback, useEffect, useState } from "react";
import { Button, CounterBadge, Link, Text } from "@fluentui/react-components";
import {
  Alert20Regular,
  Alert32Regular,
  Dismiss20Regular,
  Info20Regular,
  ShieldCheckmark20Regular,
  ShieldError20Regular,
  ShieldQuestion20Regular,
} from "@fluentui/react-icons";
import { useRouter } from "next/navigation";
import type { LiveMesh, MeshNodeRow } from "./live";
import { useLiveConnection, useNavigationState } from "./LiveConnection";
import { formatRelativeTime } from "./icons";

const NOTIFICATION_QUERY = "nodeType:Notification sort:CreatedAt-desc";

interface NotificationItem {
  path: string;
  mainNode: string;
  title: string;
  message: string;
  icon?: string;
  targetNodePath?: string;
  isRead: boolean;
  createdAt?: string;
  createdBy?: string;
  notificationType: string;
}

function str(v: unknown): string {
  return typeof v === "string" ? v : "";
}

function toItem(row: MeshNodeRow): NotificationItem | null {
  const content = (row.content ?? {}) as Record<string, unknown>;
  const path = str(row.path);
  if (!path) return null;
  return {
    path,
    mainNode: str(row.mainNode),
    title: str(content.title),
    message: str(content.message),
    icon: str(content.icon) || undefined,
    targetNodePath: str(content.targetNodePath) || undefined,
    isRead: content.isRead === true,
    createdAt: str(content.createdAt) || undefined,
    createdBy: str(content.createdBy) || undefined,
    notificationType: str(content.notificationType) || "General",
  };
}

function DefaultTypeIcon({ type }: { type: string }) {
  switch (type) {
    case "ApprovalRequired":
      return <ShieldQuestion20Regular />;
    case "ApprovalGiven":
      return <ShieldCheckmark20Regular />;
    case "ApprovalRejected":
      return <ShieldError20Regular />;
    default:
      return <Info20Regular />;
  }
}

export function NotificationCenter() {
  const live = useLiveConnection();
  const nav = useNavigationState();
  const router = useRouter();
  const mesh = live.state.kind === "live" ? live.state.mesh : null;

  const [items, setItems] = useState<NotificationItem[]>([]);
  const [open, setOpen] = useState(false);
  const [badgeCleared, setBadgeCleared] = useState(false);

  const refresh = useCallback((m: LiveMesh) => {
    m.queryNodes(NOTIFICATION_QUERY, 100).then((rows) => {
      setItems(rows.map(toItem).filter((n): n is NotificationItem => n != null));
    });
  }, []);

  // Refresh at interaction points: connection up + every navigation (see module comment).
  useEffect(() => {
    if (mesh) refresh(mesh);
  }, [mesh, nav.path, refresh]);

  const markRead = useCallback(
    (item: NotificationItem) => {
      // The canonical mutation: an RFC 7396 content merge flipping ONLY isRead — the browser twin
      // of the Blazor panel's StreamCache.Update.
      mesh?.ops.patch(item.path, { content: { isRead: true } });
    },
    [mesh],
  );

  const markAllRead = useCallback(
    (current: NotificationItem[]) => {
      let changed = false;
      for (const item of current) {
        if (!item.isRead) {
          markRead(item);
          changed = true;
        }
      }
      if (changed) setItems((prev) => prev.map((n) => (n.isRead ? n : { ...n, isRead: true })));
    },
    [markRead],
  );

  const unreadCount = badgeCleared ? 0 : items.filter((n) => !n.isRead).length;

  const toggle = () => {
    if (open) {
      setOpen(false);
      return;
    }
    // Opening acknowledges the notifications — clear the badge immediately; the panel
    // persists IsRead durably (mark-all-read once the list shows).
    setBadgeCleared(true);
    setOpen(true);
    if (mesh) refresh(mesh);
  };

  // Opening the panel counts as seeing the notifications (Blazor marks all read on the first
  // populated load).
  useEffect(() => {
    if (open && items.length > 0) markAllRead(items);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, items.length]);

  // New notifications arriving after a badge-clear should surface again.
  useEffect(() => {
    if (!open && items.some((n) => !n.isRead)) setBadgeCleared(false);
  }, [items, open]);

  const onItemClick = (item: NotificationItem) => {
    if (!item.isRead) {
      markRead(item);
      setItems((prev) => prev.map((n) => (n.path === item.path ? { ...n, isRead: true } : n)));
    }
    const target = item.targetNodePath || item.mainNode;
    if (target) {
      setOpen(false);
      router.push(`/${target}`);
    }
  };

  return (
    <>
      <Button appearance="transparent" title="Notifications" aria-label="Notifications" onClick={toggle}
        icon={
          unreadCount > 0 ? (
            <div style={{ position: "relative" }}>
              <Alert20Regular />
              <CounterBadge
                count={unreadCount}
                overflowCount={99}
                color="danger"
                size="small"
                style={{ position: "absolute", top: -6, right: -8 }}
              />
            </div>
          ) : (
            <Alert20Regular />
          )
        }
      />
      {open && (
        <>
          <div
            data-mw-notifications-overlay
            onClick={() => setOpen(false)}
            style={{ position: "fixed", inset: 0, zIndex: 500, background: "rgba(0,0,0,0.25)" }}
          />
          <div
            data-mw-notifications-panel
            role="dialog"
            aria-label="Notifications"
            style={{
              position: "fixed",
              top: 0,
              right: 0,
              bottom: 0,
              width: "min(420px, 100vw)",
              zIndex: 501,
              background: "var(--colorNeutralBackground1)",
              boxShadow: "var(--shadow28)",
              display: "flex",
              flexDirection: "column",
              padding: 16,
              overflow: "hidden",
            }}
          >
            <div style={{ display: "flex", alignItems: "center", marginBottom: 8 }}>
              <Text weight="semibold" size={400} style={{ flex: 1 }}>
                Notifications
              </Text>
              <Button
                appearance="transparent"
                icon={<Dismiss20Regular />}
                aria-label="Close"
                onClick={() => setOpen(false)}
              />
            </div>
            <div style={{ display: "flex", justifyContent: "flex-end", marginBottom: 8 }}>
              <Link onClick={() => markAllRead(items)}>Mark all read</Link>
            </div>
            {items.length === 0 ? (
              <div
                style={{
                  display: "flex",
                  flexDirection: "column",
                  alignItems: "center",
                  gap: 8,
                  padding: 32,
                  color: "var(--colorNeutralForeground3)",
                }}
              >
                <Alert32Regular />
                <p>You&rsquo;re all caught up.</p>
              </div>
            ) : (
              <div style={{ display: "flex", flexDirection: "column", gap: 6, overflowY: "auto", paddingRight: 4 }}>
                {items.map((item) => (
                  <button
                    key={item.path}
                    type="button"
                    onClick={() => onItemClick(item)}
                    style={{
                      all: "unset",
                      cursor: "pointer",
                      display: "grid",
                      gridTemplateColumns: "28px minmax(0, 1fr) auto",
                      gap: 12,
                      boxSizing: "border-box",
                      width: "100%",
                      alignItems: "start",
                      padding: item.isRead ? "10px 12px" : "10px 12px 10px 9px",
                      border: "1px solid var(--colorNeutralStroke2)",
                      borderLeft: item.isRead
                        ? "1px solid var(--colorNeutralStroke2)"
                        : "3px solid var(--colorBrandBackground)",
                      borderRadius: 8,
                      background: item.isRead ? "var(--colorNeutralBackground1)" : "var(--colorBrandBackground2)",
                    }}
                  >
                    <span
                      style={{
                        width: 28,
                        height: 28,
                        display: "inline-flex",
                        alignItems: "center",
                        justifyContent: "center",
                        borderRadius: "50%",
                        background: "var(--colorBrandBackground2)",
                        color: "var(--colorBrandForeground1)",
                      }}
                    >
                      {item.icon ? (
                        // eslint-disable-next-line @next/next/no-img-element
                        <img src={item.icon} alt="" style={{ width: 18, height: 18, objectFit: "contain" }} />
                      ) : (
                        <DefaultTypeIcon type={item.notificationType} />
                      )}
                    </span>
                    <span style={{ minWidth: 0 }}>
                      <span style={{ display: "block", fontWeight: 600, fontSize: "0.9rem", lineHeight: 1.25 }}>
                        {item.title}
                      </span>
                      {item.message && (
                        <span
                          style={{
                            display: "-webkit-box",
                            WebkitLineClamp: 2,
                            WebkitBoxOrient: "vertical",
                            overflow: "hidden",
                            fontSize: "0.82rem",
                            lineHeight: 1.35,
                            color: "var(--colorNeutralForeground2)",
                          }}
                        >
                          {item.message}
                        </span>
                      )}
                      <span
                        style={{
                          display: "flex",
                          gap: 4,
                          marginTop: 4,
                          fontSize: "0.72rem",
                          color: "var(--colorNeutralForeground3)",
                        }}
                      >
                        <span>{formatRelativeTime(item.createdAt)}</span>
                        {item.createdBy && <span>· {item.createdBy}</span>}
                      </span>
                    </span>
                    {!item.isRead && (
                      <span
                        title="Unread"
                        style={{
                          width: 8,
                          height: 8,
                          borderRadius: "50%",
                          background: "var(--colorBrandBackground)",
                          marginTop: 6,
                        }}
                      />
                    )}
                  </button>
                ))}
              </div>
            )}
          </div>
        </>
      )}
    </>
  );
}
