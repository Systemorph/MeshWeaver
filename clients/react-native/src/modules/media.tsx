// MEDIA module — Video + SlideShow, physically OUT of the core pack: this file is the only place
// the app touches expo-av's Video, so a deployment whose manifest omits `media` ships a bundle
// without it. The RN twin of the server-side media module carrying its own views.
import { useEffect, useRef } from "react";
import { Linking, Pressable, StyleSheet, Text } from "react-native";
import { Video as ExpoVideo, ResizeMode } from "expo-av";
import { str as s, useText, type ControlComponent, type DeploymentModule } from "@meshweaver/react/core";
import { parseHref, useCurrentAddress, useNavigate } from "../nav";

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

const styles = StyleSheet.create({
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

const media: DeploymentModule = { name: "media", pack: { controls: { Video, SlideShow } } };
export default media;
