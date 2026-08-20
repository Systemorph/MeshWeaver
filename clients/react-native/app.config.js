// Build-time override for the mesh a native build dials (app.json → expo.extra.portalUrl).
// A PHYSICAL device cannot reach the dev machine's sidecar at localhost — build with
//   MEMEX_PORTAL_URL=http://<mac-hostname>.local:5250 npx expo run:ios --device …
// (and start Memex.LocalMesh with Grpc:ListenLan=true) to point the app at it. Unset, the
// app.json default (the simulator-reachable localhost sidecar) applies unchanged.
module.exports = ({ config }) => ({
  ...config,
  extra: { ...config.extra, portalUrl: process.env.MEMEX_PORTAL_URL ?? config.extra?.portalUrl },
});
