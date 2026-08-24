// Vitest stand-in for expo-constants: code under test (./connection — asset-URL + portal
// resolution) reads Constants at module load, and the REAL package — SDK 57 included — reads
// `globalThis.expo.EventEmitter` at import, which only exists in a real Expo runtime. Headless
// tests get the default localhost sidecar URL.
export default { expoConfig: { extra: { portalUrl: "http://localhost:5250" } } };
