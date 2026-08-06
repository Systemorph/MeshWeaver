// Container + media leaves the RN pack was missing: NamedArea, Commentable, Redirect, Dialog,
// Video, SlideShow. Native ports of clients/react/src/controls/{containers,display}.tsx.
//
// An UNREGISTERED container is the worst kind of gap: it renders the "Unsupported" fallback and its
// child area DISAPPEARS, so a commentable node lost its whole body rather than just its comment
// button. That is exactly what the new parity ratchet caught.

import { useEffect, useRef, useState } from "react";
import { View, Text, Pressable, Modal, ScrollView, StyleSheet, Linking } from "react-native";
import { Video as ExpoVideo, ResizeMode } from "expo-av";
import {
  useLocalize,
  ControlRenderer,
  RenderArea,
  RenderChildren,
  useEmit,
  useResolve,
  useScope,
  str,
  useText,
  type ControlComponent,
} from "@meshweaver/react/core";
import { useNavigate, useCurrentAddress, parseHref } from "./nav";

const s = str;

// ── NamedArea ────────────────────────────────────────────────────────────────
/** Renders the area named by the control — the indirection Blazor's NamedAreaView performs. */
const NamedArea: ControlComponent = ({ control }) => {
  const area = useText(control.area);
  return area ? <RenderArea areaKey={area} /> : null;
};

// ── Commentable ──────────────────────────────────────────────────────────────
/**
 * A one-area container that wraps content in Blazor's select-to-comment affordance.
 *
 * Like the web pack, RN renders the WRAPPED CONTENT and does not offer the affordance — the same
 * shape as Blazor's own `CanComment: false` path, which "renders the wrapped content untouched".
 * Anchoring needs selection capture plus the `_Comment` satellite write, which this pack has no
 * surface for. Registering it is what keeps the child content on screen.
 */
const Commentable: ControlComponent = ({ control }) => <RenderChildren control={control} />;

// ── Redirect ─────────────────────────────────────────────────────────────────
/**
 * A client-side redirect link. Mesh-relative hrefs go through the RN nav seam (the same one the
 * shell drives); external / mailto hrefs open in the system browser via Linking, since there is no
 * <a href> to delegate to.
 */
const Redirect: ControlComponent = ({ control }) => {
  const href = useText(control.href);
  const navigate = useNavigate();
  const current = useCurrentAddress();
  const go = () => {
    if (!href) return;
    const target = parseHref(href, current);
    if (target) navigate(target);
    else void Linking.openURL(href).catch(() => undefined);
  };
  return (
    <Pressable accessibilityRole="link" onPress={go}>
      <Text style={styles.link}>{href}</Text>
    </Pressable>
  );
};

// ── Dialog ───────────────────────────────────────────────────────────────────
/** Blazor's DialogView size tokens → a max width. */
function dialogWidth(size: string): number {
  switch (size.toUpperCase()) {
    case "S":
      return 380;
    case "L":
      return 800;
    case "XL":
      return 1024;
    default:
      return 600;
  }
}

/**
 * A real modal — the native mirror of Blazor's DialogView: shown on mount, title in the header,
 * ContentArea in the body, ActionsArea in the footer when HasActions (else a Close button when
 * IsClosable), and a CloseDialogEvent posted back to the owning hub on dismissal.
 */
const Dialog: ControlComponent = ({ control }) => {
  const t = useLocalize();
  const [open, setOpen] = useState(true);
  const emit = useEmit();
  const { area } = useScope();
  const title = useText(control.title);
  const size = s(useResolve(control.size)) || "M";
  const isClosable = !!useResolve(control.isClosable);
  const hasActions = !!useResolve(control.hasActions);
  const contentArea = (control.contentArea as { area?: unknown } | undefined)?.area;
  const actionsArea = (control.actionsArea as { area?: unknown } | undefined)?.area;
  const close = (state: "OK" | "Cancel") => {
    setOpen(false);
    emit({ kind: "closeDialog", area, value: state });
  };
  return (
    <Modal visible={open} transparent animationType="fade" onRequestClose={() => close("Cancel")}>
      <View style={styles.dialogScrim}>
        <View style={[styles.dialogSurface, { maxWidth: dialogWidth(size) }]}>
          {title ? <Text style={styles.dialogTitle}>{title}</Text> : null}
          <ScrollView style={styles.dialogBody}>
            {contentArea ? <RenderArea areaKey={String(contentArea)} /> : null}
          </ScrollView>
          {hasActions && actionsArea ? (
            <View style={styles.dialogActions}>
              <RenderArea areaKey={String(actionsArea)} />
            </View>
          ) : isClosable ? (
            <View style={styles.dialogActions}>
              <Pressable style={styles.dialogButton} onPress={() => close("OK")}>
                <Text style={styles.dialogButtonText}>{t("common.close")}</Text>
              </Pressable>
            </View>
          ) : null}
        </View>
      </View>
    </Modal>
  );
};

// ── Video ────────────────────────────────────────────────────────────────────
/**
 * Native video playback (expo-av). `Kind: "embed"` on the web renders an <iframe> for a YouTube /
 * Vimeo page URL — there is no native iframe, so an embed opens in the system browser behind a
 * poster-style press target. A direct media Src plays inline with native controls.
 */
const Video: ControlComponent = ({ control }) => {
  const src = useText(control.src);
  const poster = useText(control.poster);
  const title = useText(control.title);
  const isEmbed = s(control.kind).toLowerCase() === "embed";
  // Blazor renders nothing for an empty Src — hooks are all above, so the early return stays legal.
  if (!src) return null;
  if (isEmbed) {
    return (
      <Pressable accessibilityRole="link" style={styles.videoEmbed} onPress={() => void Linking.openURL(src).catch(() => undefined)}>
        <Text style={styles.videoEmbedGlyph}>▶</Text>
        <Text style={styles.videoEmbedLabel}>{title || src}</Text>
      </Pressable>
    );
  }
  return (
    <ExpoVideo
      style={styles.video}
      source={{ uri: src }}
      posterSource={poster ? { uri: poster } : undefined}
      usePoster={!!poster}
      useNativeControls
      resizeMode={ResizeMode.CONTAIN}
      accessibilityLabel={title || undefined}
    />
  );
};

// ── SlideShow ────────────────────────────────────────────────────────────────
/** Presentation keys → action, mirroring SlideShowView.razor.js's `actionForKey`. */
const PRESENT_KEY_ACTIONS: Record<string, "next" | "prev" | "first" | "last" | "exit"> = {
  ArrowRight: "next",
  ArrowDown: "next",
  PageDown: "next",
  " ": "next",
  Spacebar: "next",
  Enter: "next",
  ArrowLeft: "prev",
  ArrowUp: "prev",
  PageUp: "prev",
  Home: "first",
  End: "last",
  Escape: "exit",
  Esc: "exit",
};

/**
 * The presenter-mode driver placed in a deck's `Present` area. Like Blazor's SlideShowView it
 * renders NO chrome — it only binds the PowerPoint keys to the hrefs the control carries, and a
 * null href makes that key a no-op (Next is null on the last slide).
 *
 * Keys only exist where there is a keyboard: on Expo web (`document` present) the listener binds;
 * on a touch device it does not, and the deck is driven by the surrounding navigation controls.
 */
const SlideShow: ControlComponent = ({ control }) => {
  const navigate = useNavigate();
  const current = useCurrentAddress();
  const first = useText(control.firstHref);
  const previous = useText(control.previousHref);
  const next = useText(control.nextHref);
  const last = useText(control.lastHref);
  const exit = useText(control.exitHref);

  // Read the current hrefs from a ref so the listener binds ONCE — a slide change updates the ref
  // rather than tearing down and re-adding the listener.
  const hrefs = useRef({ first, previous, next, last, exit });
  hrefs.current = { first, previous, next, last, exit };
  const addr = useRef(current);
  addr.current = current;

  useEffect(() => {
    const doc = typeof document === "undefined" ? null : document;
    if (!doc) return; // native: no keyboard to bind
    const onKeyDown = (e: KeyboardEvent) => {
      const target = e.target as { tagName?: string; isContentEditable?: boolean } | null;
      if (target && (target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName ?? "")))
        return; // never hijack keys while the user is typing
      const action = PRESENT_KEY_ACTIONS[e.key];
      if (!action) return;
      const h = hrefs.current;
      const href = action === "next" ? h.next : action === "prev" ? h.previous : h[action];
      if (!href) return; // a null href disables that key
      e.preventDefault();
      const target2 = parseHref(href, addr.current);
      if (target2) navigate(target2);
      else void Linking.openURL(href).catch(() => undefined);
    };
    doc.addEventListener("keydown", onKeyDown);
    return () => doc.removeEventListener("keydown", onKeyDown);
  }, [navigate]);

  return null;
};

export const rnContainerControls: Record<string, ControlComponent> = {
  NamedArea,
  Commentable,
  Redirect,
  Dialog,
  Video,
  SlideShow,
};

const styles = StyleSheet.create({
  link: { fontSize: 14, color: "#0f6cbd", textDecorationLine: "underline" },
  dialogScrim: { flex: 1, backgroundColor: "rgba(0,0,0,0.4)", alignItems: "center", justifyContent: "center", padding: 16 },
  dialogSurface: { width: "100%", backgroundColor: "white", borderRadius: 8, padding: 16, gap: 12, maxHeight: "85%" },
  dialogTitle: { fontSize: 18, fontWeight: "700", color: "#242424" },
  dialogBody: { flexGrow: 0 },
  dialogActions: { flexDirection: "row", justifyContent: "flex-end", gap: 8 },
  dialogButton: { backgroundColor: "#edebe9", paddingVertical: 8, paddingHorizontal: 14, borderRadius: 6 },
  dialogButtonText: { color: "#242424", fontWeight: "600" },
  video: { width: "100%", aspectRatio: 16 / 9, backgroundColor: "#000", borderRadius: 6 },
  videoEmbed: {
    width: "100%",
    aspectRatio: 16 / 9,
    backgroundColor: "#000",
    borderRadius: 6,
    alignItems: "center",
    justifyContent: "center",
    gap: 8,
  },
  videoEmbedGlyph: { color: "white", fontSize: 34 },
  videoEmbedLabel: { color: "#e1e1e1", fontSize: 13, paddingHorizontal: 12, textAlign: "center" },
});
