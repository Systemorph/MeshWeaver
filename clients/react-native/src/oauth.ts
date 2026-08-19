// OAuth sign-in against any MeshWeaver portal — the same authorization server the mesh
// already runs for its MCP clients (/.well-known/oauth-authorization-server, /register,
// /authorize, /token). The app registers itself per portal via DYNAMIC CLIENT REGISTRATION
// (RFC 7591, verified live on memex), opens the system browser for the portal's normal
// sign-in (SSO included), and exchanges the code with PKCE. The resulting access token is
// the same Bearer every surface accepts (gRPC-web, /api/mesh, /api/speech/transcribe).

import * as AuthSession from "expo-auth-session";
import * as WebBrowser from "expo-web-browser";

WebBrowser.maybeCompleteAuthSession();

export interface OAuthResult {
  accessToken: string;
  refreshToken?: string;
  expiresAt?: number;   // epoch ms
  clientId: string;
}

interface ServerMetadata {
  authorization_endpoint: string;
  token_endpoint: string;
  registration_endpoint?: string;
}

const CLIENT_KEY = "meshweaver.oauth.clients";   // { [portalUrl]: clientId }

function readClients(): Record<string, string> {
  try { return JSON.parse(localStorage.getItem(CLIENT_KEY) ?? "{}"); } catch { return {}; }
}

function writeClient(url: string, clientId: string): void {
  try {
    const all = readClients(); all[url] = clientId;
    localStorage.setItem(CLIENT_KEY, JSON.stringify(all));
  } catch { /* storage unavailable — re-register next time */ }
}

export const redirectUri = AuthSession.makeRedirectUri({ scheme: "memex", path: "oauth/callback" });

async function metadata(portalUrl: string): Promise<ServerMetadata> {
  const response = await fetch(`${portalUrl}/.well-known/oauth-authorization-server`);
  if (!response.ok) throw new Error(`This portal does not offer OAuth sign-in (HTTP ${response.status}).`);
  return (await response.json()) as ServerMetadata;
}

async function clientIdFor(portalUrl: string, meta: ServerMetadata): Promise<string> {
  const cached = readClients()[portalUrl];
  if (cached) return cached;
  const registration = meta.registration_endpoint ?? `${portalUrl}/register`;
  const response = await fetch(registration, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      client_name: "Memex Mobile",
      redirect_uris: [redirectUri],
      grant_types: ["authorization_code", "refresh_token"],
      response_types: ["code"],
      token_endpoint_auth_method: "none",
    }),
  });
  if (!response.ok) throw new Error(`Client registration failed (HTTP ${response.status}).`);
  const { client_id } = (await response.json()) as { client_id: string };
  writeClient(portalUrl, client_id);
  return client_id;
}

/** Browser sign-in: registers (once), authorizes with PKCE, exchanges the code. Throws with
 *  a speakable message on every failure path; returns tokens on success. */
export async function signInWithOAuth(portalUrl: string): Promise<OAuthResult> {
  const base = portalUrl.replace(/\/+$/, "");
  const meta = await metadata(base);
  const clientId = await clientIdFor(base, meta);

  const request = new AuthSession.AuthRequest({
    clientId,
    redirectUri,
    responseType: AuthSession.ResponseType.Code,
    usePKCE: true,
    scopes: [],
  });
  const result = await request.promptAsync({ authorizationEndpoint: meta.authorization_endpoint });
  if (result.type !== "success" || !result.params.code)
    throw new Error(result.type === "cancel" || result.type === "dismiss"
      ? "Sign-in was cancelled." : `Sign-in failed (${result.type}).`);

  const tokens = await AuthSession.exchangeCodeAsync(
    { clientId, code: result.params.code, redirectUri,
      extraParams: { code_verifier: request.codeVerifier ?? "" } },
    { tokenEndpoint: meta.token_endpoint },
  );
  if (!tokens.accessToken) throw new Error("The token exchange returned no access token.");
  return {
    accessToken: tokens.accessToken,
    refreshToken: tokens.refreshToken ?? undefined,
    expiresAt: tokens.expiresIn ? Date.now() + tokens.expiresIn * 1000 : undefined,
    clientId,
  };
}

/** Refresh an expired access token; returns null when the refresh token itself is dead
 *  (the caller falls back to a fresh browser sign-in). */
export async function refreshOAuth(portalUrl: string, clientId: string, refreshToken: string): Promise<OAuthResult | null> {
  try {
    const meta = await metadata(portalUrl.replace(/\/+$/, ""));
    const tokens = await AuthSession.refreshAsync(
      { clientId, refreshToken }, { tokenEndpoint: meta.token_endpoint });
    if (!tokens.accessToken) return null;
    return {
      accessToken: tokens.accessToken,
      refreshToken: tokens.refreshToken ?? refreshToken,
      expiresAt: tokens.expiresIn ? Date.now() + tokens.expiresIn * 1000 : undefined,
      clientId,
    };
  } catch {
    return null;
  }
}
