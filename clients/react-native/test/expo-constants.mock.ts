// Vitest stand-in for expo-constants: rnMeshLive/nativeHtml import ./connection (asset-URL
// resolution against the current instance), which reads the configured portal URL off Constants at
// module load. Tests run headless — the default localhost sidecar URL is all they need.
export default { expoConfig: { extra: { portalUrl: "http://localhost:5250" } } };
