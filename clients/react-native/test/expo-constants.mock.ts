// Vitest stand-in for expo-constants: rnMeshLive imports ./connection (for the icon grid's URL
// resolution), which reads the configured portal URL off Constants at module load. Tests run
// headless — the default localhost sidecar URL is all they need.
export default { expoConfig: { extra: { portalUrl: "http://localhost:5250" } } };
