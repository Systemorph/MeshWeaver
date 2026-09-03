using System.Security.Cryptography;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The install's key-protection master key when the DEPLOYMENT did not supply one — a file beside
/// the instance manifest, holding nothing but the base64 key.
///
/// <para><b>Why a separate file rather than a field on the manifest.</b> The manifest is a RECORD:
/// it gets copied onto a new volume, backed up, diffed, and pasted into an issue when an instance
/// will not boot. Every one of those is a reason the key must not be in it — a key stored beside
/// the ciphertext it unlocks turns "encrypted at rest" into a spelling of "plaintext". Two files
/// means the manifest can travel and the key cannot follow by accident.</para>
///
/// <para>🚨 <b>The deployment's own key ALWAYS wins.</b> On Kubernetes
/// <c>Ai__KeyProtection__MasterKey</c> comes from a Secret and only the ciphertext is on the PVC,
/// which is a real separation; this file exists for the install that has no secret store — a laptop
/// — where the honest position is that both live on one disk, mode <c>0600</c>, exactly as every
/// local credential file does. <see cref="Resolve"/> never overrides a configured key, and
/// <see cref="EnsureCreated"/> is only ever reached when none is configured.</para>
///
/// <para>The alternative was to let <c>ProviderKeyProtector.Protect</c> throw on a fresh install —
/// which it does, deliberately, rather than store a credential unprotected. A setup wizard whose
/// first act is to collect API keys must therefore provision a key before it collects one, and this
/// is that provisioning.</para>
/// </summary>
public static class InstanceMasterKey
{
    /// <summary>The key file's name on the writable root.</summary>
    public const string FileName = "instance.key";

    /// <summary>AES-256: the size <c>ProviderKeyProtector</c> requires.</summary>
    private const int KeyBytes = 32;

    /// <summary>The key file's path under <paramref name="rootDirectory"/>.</summary>
    /// <param name="rootDirectory">The writable root (<c>ModuleRoot.Resolve</c>).</param>
    public static string PathFor(string rootDirectory) =>
        Path.Combine(rootDirectory, FileName);

    /// <summary>
    /// The key this install should use: the one the deployment configured, else the one on disk,
    /// else null.
    ///
    /// <para>Never generates. A read that silently minted a key would make "the deployment's secret
    /// did not reach the pod" indistinguishable from "this install has no secret store", and the
    /// first of those must stay loud — a portal that quietly re-keys cannot decrypt anything it
    /// wrote before.</para>
    /// </summary>
    /// <param name="rootDirectory">The writable root.</param>
    /// <param name="configured">The value of <c>Ai:KeyProtection:MasterKey</c> as the host's own
    /// configuration sources answer it. Blank is treated as absent — an env var cannot be null,
    /// only empty.</param>
    /// <returns>The base64 master key, or null when this install has none.</returns>
    public static string? Resolve(string rootDirectory, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        var path = PathFor(rootDirectory);
        try
        {
            if (!File.Exists(path))
                return null;
            var text = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The install's key, generating and persisting one when it has none. Idempotent: an existing
    /// key — configured or on disk — is returned unchanged, because re-keying would strand every
    /// secret already written under the old one.
    /// </summary>
    /// <param name="rootDirectory">The writable root. Created when absent.</param>
    /// <param name="configured">The deployment's own key, which wins and is never persisted here.</param>
    /// <returns>The base64 master key. Never null.</returns>
    /// <exception cref="IOException">The root is not writable and no key was configured — an
    /// instance that can neither be given a key nor mint one cannot store a credential at all, and
    /// saying so here beats a throw four steps later inside the protector.</exception>
    public static string EnsureCreated(string rootDirectory, string? configured)
    {
        if (Resolve(rootDirectory, configured) is { } existing)
            return existing;

        Directory.CreateDirectory(rootDirectory);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes));
        var path = PathFor(rootDirectory);
        // Same atomic write the manifest uses: a crash mid-write must not leave a truncated key,
        // which would read as a DIFFERENT key and silently fail to decrypt everything.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, key);
        RestrictToOwner(temp);
        File.Move(temp, path, overwrite: true);
        return key;
    }

    /// <summary>
    /// Narrows the file to the owner (<c>0600</c>) on the platforms that have Unix modes. A no-op on
    /// Windows, where the inherited ACL of a per-deployment directory is the equivalent control —
    /// silently, because failing the boot of an instance over a permission bit would be worse than
    /// the exposure it prevents on a single-user machine.
    /// </summary>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Best effort: a filesystem that cannot express the mode (a mounted SMB share on AKS is
            // the live case) still stores the key. The separation that matters there is that the
            // deployment supplies its own key from a Secret and this file is never written at all.
        }
    }
}
