using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.Mesh;

/// <summary>
/// What a NEW INSTANCE was set up to be — the durable answer to "which database, which modules
/// boot, which packages are provisioned, and which of those land for every user" (#2550).
///
/// <para>🚨 <b>A FILE, not a mesh node, and for the same reason the module activation sidecar is
/// one:</b> it is consumed before any storage provider, hub, or connection string exists. At the
/// moment this is read there is by definition no database to read a node from — choosing the
/// database is what it answers. It lives on the writable root beside <c>modules/</c> so it travels
/// with the deployment it describes.</para>
///
/// <para><b>Two authors, one artifact.</b> The interactive setup wizard writes this by hand on an
/// empty image; a fleet <c>Hosting/Deployment</c> record renders the identical file when it
/// provisions an instance. That is deliberate — a second provisioning path that produced a
/// different shape is how the interactive and fleet routes drift until only one of them works.</para>
///
/// <para><b>Absent is not an error.</b> Every deployment configured through appsettings today has
/// no manifest and must keep booting exactly as it does. The manifest only ANSWERS what
/// configuration has not already said; it never overrides a host that stated its own storage.</para>
/// </summary>
public sealed record InstanceManifest
{
    /// <summary>The manifest's file name on the writable root.</summary>
    public const string FileName = "instance.json";

    /// <summary>
    /// The setup state this manifest records. A manifest that exists but is not
    /// <see cref="InstanceSetupState.Complete"/> keeps the instance in SETUP — a half-answered
    /// wizard must not boot a half-configured mesh.
    /// </summary>
    public InstanceSetupState State { get; init; } = InstanceSetupState.AwaitingStorage;

    /// <summary>The storage the operator chose. Null until the wizard's first step is answered.</summary>
    public InstanceStorageSelection? Storage { get; init; }

    /// <summary>
    /// Module entry assemblies this instance BOOTS — the <c>Modules:Assemblies</c> lane, loaded
    /// into the process. These are the ones the image ships and the deployment turns on.
    /// </summary>
    public ImmutableList<string> BootModules { get; init; } = [];

    /// <summary>
    /// Packages PROVISIONED into the mesh at first boot — Store installs, the registry lane. A
    /// package here is content (and possibly a module) landed once for the instance, not a DLL
    /// listed for the loader; those are two different mechanisms and the wizard keeps them apart.
    /// </summary>
    public ImmutableList<string> ProvisionPackages { get; init; } = [];

    /// <summary>
    /// Packages PRE-INSTALLED FOR USERS — landed per user rather than once per instance. Recorded
    /// as the deployment's answer; the package's own <c>preInstalled</c> declaration is the
    /// platform baseline, and this is the per-instance addition to it.
    /// </summary>
    public ImmutableList<string> UserPreInstallPackages { get; init; } = [];

    /// <summary>
    /// Which logins this instance offers. Null means the wizard never answered the question, and
    /// the host's own configuration decides alone — which is every deployment that exists today.
    /// </summary>
    public InstanceSignInSelection? SignIn { get; init; }

    /// <summary>
    /// The model providers and the embeddings endpoint. Null means no model was configured at
    /// setup: a valid instance, and one the portal already warns about at boot.
    /// </summary>
    public InstanceAiSelection? Ai { get; init; }

    /// <summary>Who completed setup, for provenance. Never a credential.</summary>
    public string? SetUpBy { get; init; }

    /// <summary>When setup completed.</summary>
    public DateTimeOffset? SetUpAt { get; init; }

    /// <summary>Whether this manifest answers the storage question — the gate that decides
    /// whether the instance may leave setup mode. Pure.</summary>
    [JsonIgnore]
    public bool HasStorage => Storage is { } s && !string.IsNullOrWhiteSpace(s.Type);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The manifest's path under <paramref name="rootDirectory"/>.</summary>
    public static string PathFor(string rootDirectory) =>
        Path.Combine(rootDirectory, FileName);

    /// <summary>
    /// Reads the manifest, or null when this instance has none — which is the ordinary state of
    /// every deployment configured through appsettings.
    ///
    /// <para>🚨 An UNREADABLE manifest is not the same as an absent one, and the difference decides
    /// whether an instance serves. Absent means "configured elsewhere, carry on"; corrupt or
    /// unreadable means "this instance was set up and we cannot tell how", which must surface
    /// through <paramref name="onUnreadable"/> and keep the instance in SETUP rather than boot it
    /// as if it had never been configured — booting a configured instance into a fresh setup wizard
    /// would invite an operator to re-answer questions against a database that already holds data.</para>
    /// </summary>
    /// <param name="rootDirectory">The writable root (<c>ModuleRoot.Resolve</c>).</param>
    /// <param name="onUnreadable">Called with a human-readable reason when a manifest EXISTS but
    /// cannot be read. Pre-DI, so production passes stderr.</param>
    /// <returns>The manifest, or null when the file does not exist.</returns>
    public static InstanceManifest? Read(string rootDirectory, Action<string>? onUnreadable = null)
    {
        var path = PathFor(rootDirectory);
        string text;
        try
        {
            if (!File.Exists(path))
                return null;
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Vanished between the check and the read — genuinely absent.
            return null;
        }
        catch (Exception ex)
        {
            onUnreadable?.Invoke(
                $"The instance manifest at '{path}' exists but could not be READ "
                + $"({ex.GetType().Name}: {ex.Message}). This instance stays in SETUP: it was "
                + "configured once, and booting it as if it never had been would offer a fresh "
                + "setup over a database that may already hold data.");
            return Unreadable;
        }

        try
        {
            return JsonSerializer.Deserialize<InstanceManifest>(text, Json) ?? Unreadable;
        }
        catch (JsonException ex)
        {
            onUnreadable?.Invoke(
                $"The instance manifest at '{path}' is malformed ({ex.Message}). This instance "
                + "stays in SETUP — repair or delete the file to re-run setup.");
            return Unreadable;
        }
    }

    /// <summary>
    /// The manifest a corrupt file resolves to: it EXISTS (so the instance is not treated as
    /// unconfigured) but answers nothing (so it cannot leave setup). Never written to disk.
    /// </summary>
    public static InstanceManifest Unreadable { get; } =
        new() { State = InstanceSetupState.Unreadable };

    /// <summary>
    /// Writes the manifest atomically — a temp file in the same directory, then a rename — so a
    /// crash mid-write leaves the previous answer intact rather than a truncated file that reads
    /// as <see cref="Unreadable"/> and parks the instance in setup.
    /// </summary>
    public void Write(string rootDirectory)
    {
        Directory.CreateDirectory(rootDirectory);
        var path = PathFor(rootDirectory);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>How far setup has got. The instance serves normally only at
/// <see cref="Complete"/>.</summary>
public enum InstanceSetupState
{
    /// <summary>The storage question is unanswered — the wizard's first step.</summary>
    AwaitingStorage,

    /// <summary>Storage is chosen; the module questions are unanswered.</summary>
    AwaitingModules,

    /// <summary>Setup finished. The next boot configures the mesh from this manifest.</summary>
    Complete,

    /// <summary>A manifest exists but could not be read. Not a state the wizard writes — it is
    /// what a corrupt file resolves to, and it keeps the instance in setup deliberately.</summary>
    Unreadable,
}

/// <summary>
/// The storage an instance was set up with — the answer to "which database", in the shape
/// <c>Graph:Storage</c> already takes.
///
/// <para>🚨 <b>A connection string is a SECRET and a manifest is a record.</b> A deployment that
/// names <see cref="SecretName"/> keeps the value in its secret store, exactly as
/// <c>DeploymentContent</c>'s mounts do; <see cref="ConnectionString"/> exists for local and
/// single-operator installs where there is no secret store to name, and is the reason a manifest
/// must never be synced to git.</para>
/// </summary>
public sealed record InstanceStorageSelection
{
    /// <summary>
    /// The backend key — the value <c>Graph:Storage:Type</c> takes, resolved against the KEYED
    /// <c>IStorageAdapterFactory</c> registrations this image ships. Discovered, never hardcoded:
    /// an image without the Cosmos module must not be able to record Cosmos here.
    /// </summary>
    public string Type { get; init; } = "";

    /// <summary>The connection string, for installs with no secret store. Mutually exclusive with
    /// <see cref="SecretName"/> — see the type remarks.</summary>
    public string? ConnectionString { get; init; }

    /// <summary>The NAME of the secret carrying the connection string. Never a value.</summary>
    public string? SecretName { get; init; }

    /// <summary>Base path, for the file-system backend.</summary>
    public string? BasePath { get; init; }
}
