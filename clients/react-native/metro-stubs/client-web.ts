// Offline-run stub for @meshweaver/client-web. The real client is the gRPC-web (Connect-ES) transport —
// browser/Node-oriented, and on React Native it needs a streaming-`fetch` polyfill for the Connect
// server-stream (see clients/grpc-web/README + clients/react-native/README "Live transport"). The bundled
// demo runs OFFLINE (StaticAreaSource), so live mode isn't wired in; Metro aliases the live client to this
// stub so connect-web isn't pulled into the RN bundle. createLiveSource() is never called while App.tsx's
// LIVE is null; if it is, connect() explains what to wire.

export type MeshWebConnection = { readonly address: string; close(): void };

export function connect(): Promise<never> {
  return Promise.reject(
    new Error(
      "Live mode is not bundled in this offline demo. To go live, wire @meshweaver/client-web " +
        "(clients/grpc-web) into metro.config.js and add a streaming-fetch polyfill — see the README.",
    ),
  );
}
