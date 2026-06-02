namespace MeshWeaver.AI.Connect;

/// <summary>
/// The seam by which a completed CLI login persists its captured token. The
/// <see cref="ConnectSessionManager"/> (in <c>MeshWeaver.AI</c>) hands the raw token here once a
/// strategy captures it; the portal's implementation stores it as an encrypted
/// <c>ModelProvider</c> node (create-or-rotate) via <c>ModelProviderService</c> — so the AI layer
/// never references the portal assembly. Reactive end-to-end (no <c>Task</c>).
/// </summary>
public interface IConnectTokenSink
{
    /// <summary>
    /// Persist the captured CLI subscription token for <paramref name="ownerPath"/> under provider
    /// <paramref name="providerName"/> (<c>"ClaudeCode"</c> / <c>"Copilot"</c>). Creates the
    /// <c>ModelProvider</c> node when absent, rotates the key when it already exists. The token is
    /// encrypted at rest by the implementation. Emits the persisted provider node path + an 8-char
    /// key fingerprint exactly once.
    /// </summary>
    IObservable<(string ProviderNodePath, string KeyFingerprint)> StoreToken(
        string ownerPath, string providerName, string token);
}
