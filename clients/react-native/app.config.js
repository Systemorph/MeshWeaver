// Expo config = the DEPLOYMENT's identity. Reads the same deployment manifest the bundle is
// composed from (deployment/*.json, selected by MEMEX_DEPLOYMENT — see scripts/gen-deployment.mjs),
// so ONE file decides the app a deployment ships: display name, the mesh a native build dials, and
// (via the generated module) the client modules + branding in the bundle. Precedence for the portal:
// MEMEX_PORTAL_URL env (a one-off build override, e.g. pointing a device at this Mac's sidecar:
//   MEMEX_PORTAL_URL=http://<mac-hostname>.local:5250 npx expo run:ios --device …
// with Grpc:ListenLan=true on the sidecar) > manifest portalUrl > app.json extra.
const fs = require("node:fs");
const path = require("node:path");

function readManifest() {
  const sel = process.env.MEMEX_DEPLOYMENT ?? "default";
  const file = sel.endsWith(".json")
    ? path.resolve(__dirname, sel)
    : path.join(__dirname, "deployment", `${sel}.json`);
  try {
    return JSON.parse(fs.readFileSync(file, "utf8"));
  } catch {
    return {};
  }
}

module.exports = ({ config }) => {
  const manifest = readManifest();
  return {
    ...config,
    name: manifest.branding?.displayName ?? manifest.name ?? config.name,
    extra: {
      ...config.extra,
      portalUrl: process.env.MEMEX_PORTAL_URL ?? manifest.portalUrl ?? config.extra?.portalUrl,
    },
  };
};
