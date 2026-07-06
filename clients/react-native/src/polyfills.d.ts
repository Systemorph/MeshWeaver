// Ambient module declarations for the React-Native-only streaming-fetch polyfills (no shipped types).
// These are consumed exclusively by nativeFetch.native.ts; the web build never bundles them.
declare module "react-native-fetch-api" {
  export const fetch: (input: any, init?: any) => Promise<any>;
}
declare module "web-streams-polyfill" {
  export const ReadableStream: any;
}
declare module "text-encoding" {
  export const TextEncoder: any;
  export const TextDecoder: any;
}
// Native-only HTML renderer (consumed exclusively by nativeHtml.native.tsx; the web build never bundles it).
declare module "react-native-render-html" {
  const RenderHtml: any;
  export default RenderHtml;
}
