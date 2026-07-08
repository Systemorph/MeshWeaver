// Default (web / react-native-web): the browser fetch already exposes a readable response body, so the
// gRPC-web Connect server-stream works with no override. Metro/Expo picks nativeFetch.native.ts on a
// device instead (streaming-fetch polyfill); tsc resolves this file, so keep the signatures identical.
export function nativeStreamingFetch(): typeof globalThis.fetch | undefined {
  return undefined;
}
