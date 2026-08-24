// Vitest stand-ins for the OAuth deps ./connection pulls in via ./oauth. Tests never sign in.
export const maybeCompleteAuthSession = () => {};
export const makeRedirectUri = () => "memex://oauth/callback";
export class AuthRequest { codeVerifier = ""; promptAsync = async () => ({ type: "cancel" }); }
export const ResponseType = { Code: "code" };
export const exchangeCodeAsync = async () => ({ accessToken: "" });
export const refreshAsync = async () => ({ accessToken: "" });
export default {};
