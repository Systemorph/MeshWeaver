// The ONE native icon glyph — the RN twin of the web pack's MeshIcon, over the SAME shared
// classification (classifyIcon): inline `<svg>` markup and `.svg` URLs render through
// react-native-svg (RN's Image cannot decode SVG — the reason the colorful node-icon set showed
// as blanks), raster URLs through Image, emoji as text, a Fluent NAME as a neutral chip (no DOM
// icon set on native). Relative URLs resolve against the CURRENT instance (resolveAssetUrl) — a
// device has no origin of its own. Used by the Icon control leaf, the left-menu rows, and any
// leaf that needs an icon string drawn.
import { Image, Text, View } from "react-native";
import { SvgUri, SvgXml } from "react-native-svg";
import { classifyIcon, sizeInlineSvg } from "@meshweaver/react/core";
import { resolveAssetUrl } from "./connection";
import type { ReactNode } from "react";

export function IconGlyph({ icon, size = 20 }: { icon?: string; size?: number }): ReactNode {
  const classified = classifyIcon((icon ?? "") as never);
  switch (classified.kind) {
    case "svg":
      // sizeInlineSvg: authored root width/height intrinsics would win over the props and
      // paint e.g. 24px inside a 64px tile — force the requested size on the root tag.
      return <SvgXml xml={sizeInlineSvg(classified.text, size)} width={size} height={size} />;
    case "url": {
      const url = resolveAssetUrl(classified.text);
      return /\.svg(\?|#|$)/i.test(url)
        ? <SvgUri uri={url} width={size} height={size} />
        : <Image source={{ uri: url }} style={{ width: size, height: size, resizeMode: "contain" }} />;
    }
    case "emoji":
      return <Text style={{ fontSize: size * 0.9, lineHeight: size * 1.15 }}>{classified.text}</Text>;
    case "fluent":
      return (
        <View style={{ width: size, height: size, alignItems: "center", justifyContent: "center" }}>
          <Text style={{ fontSize: size * 0.7, color: "#8a8a8a" }}>▨</Text>
        </View>
      );
    default:
      return null;
  }
}
