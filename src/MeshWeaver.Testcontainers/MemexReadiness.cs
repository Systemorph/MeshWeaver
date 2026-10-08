using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace MeshWeaver.Testcontainers;

/// <summary>
/// What a memex container's HTTP surface says about whether its MESH has started — not merely its
/// process (MeshWeaver#6037).
///
/// <para>🚨 <b>A probe path answering 200 proves nothing about the mesh.</b> Two hosts answer every
/// probe path with a flat <c>200 ok</c> and neither has composed a mesh: the first-run setup
/// wizard (<c>SetupOnlyHost</c> maps <c>/healthz</c>, <c>/health</c>, <c>/alive</c> and
/// <c>/ready</c> to <c>ok</c> and redirects every other path to <c>/setup</c>), and the GUI host's
/// <c>/healthz</c> short-circuit, which answers before identity, the mesh and the renderer by
/// design. This helper used to wait on <c>/healthz</c>, and because its builder never stated
/// <c>Graph:Storage:Type</c> the image ALWAYS booted the wizard — so every green start proved a
/// started container and nothing more.</para>
///
/// <para>The verdict is therefore read off two things only a composed host produces:
/// <list type="bullet">
/// <item><b>The wizard, named and refused.</b> A non-probe path (<see cref="MeshPath"/>) answering a
/// redirect to <c>/setup</c> is the setup-only host. That host never becomes a mesh without a person
/// submitting the form, so it is a definitive <see cref="MemexReadinessState.SetupWizard"/> — the
/// wait throws at once instead of polling to its timeout.</item>
/// <item><b>The startup census.</b> A composed host serves <c>/health</c> through the
/// health-check report writer, whose first line is the aggregate status word (<c>Healthy</c> or
/// <c>Degraded</c>; <c>Unhealthy</c> answers 503) over every registered check — the database, the
/// schema, the mesh. The wizard's <c>ok</c> is not a status word, so it can never satisfy this.</item>
/// </list></para>
/// </summary>
public static class MemexReadiness
{
    /// <summary>The startup census the verdict reads — the chart's <c>startupProbe</c> path.</summary>
    public const string HealthPath = "/health";

    /// <summary>
    /// A path no probe owns, answered by a composed host's own endpoint and redirected to
    /// <c>/setup</c> by the wizard — the discriminator between the two hosts.
    /// </summary>
    public const string MeshPath = "/api/version";

    /// <summary>The wizard's surface; a redirect here names the setup-only host.</summary>
    public const string SetupPath = "/setup";

    /// <summary>
    /// The verdict from the two responses. Pure, so the decision is pinned without Docker.
    /// </summary>
    /// <param name="meshPathStatus">The status <see cref="MeshPath"/> answered, redirects NOT followed.</param>
    /// <param name="meshPathLocation">That response's <c>Location</c>, if any.</param>
    /// <param name="healthStatus">The status <see cref="HealthPath"/> answered.</param>
    /// <param name="healthBody">That response's body.</param>
    /// <returns>The state, with the reason a reader needs when it is not <see cref="MemexReadinessState.MeshStarted"/>.</returns>
    public static MemexReadinessVerdict Classify(
        int meshPathStatus, string? meshPathLocation, int healthStatus, string? healthBody)
    {
        if (meshPathStatus is >= 300 and < 400 && IsSetupLocation(meshPathLocation))
            return new(MemexReadinessState.SetupWizard,
                $"{MeshPath} redirected to {meshPathLocation}: the container is serving the FIRST-RUN "
                + "SETUP wizard and composed no mesh. It will not become a mesh without a person "
                + "submitting the form — the instance has no Graph:Storage:Type (MemexBuilder."
                + "WithPostgres states it; a WithEnvironment that clears it lands here).");

        var firstLine = (healthBody ?? "").Split('\n', 2)[0].Trim();
        if (healthStatus == 200 && firstLine is "Healthy" or "Degraded")
            return new(MemexReadinessState.MeshStarted,
                $"{HealthPath} answered 200 with the startup census '{firstLine}'");

        return new(MemexReadinessState.NotYet,
            $"{HealthPath} answered {healthStatus} '{Clip(firstLine)}' — not the startup census of a "
            + "composed mesh (Healthy/Degraded) yet");
    }

    /// <summary>
    /// Probes a running memex over HTTP and classifies it. Redirects are never followed: the
    /// redirect to <c>/setup</c> IS the evidence that names the wizard.
    /// </summary>
    /// <param name="baseAddress">The instance's HTTP root as seen from the caller.</param>
    /// <param name="ct">Cancels the probe.</param>
    /// <returns>The verdict; a refused connection is <see cref="MemexReadinessState.NotYet"/>.</returns>
    public static async Task<MemexReadinessVerdict> ProbeAsync(Uri baseAddress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
        return await ProbeAsync(http, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Probes through a caller's client — which must NOT follow redirects, or the wizard's redirect
    /// to <c>/setup</c> is consumed before it can be read.
    /// </summary>
    /// <param name="http">A client whose <c>BaseAddress</c> is the instance's HTTP root.</param>
    /// <param name="ct">Cancels the probe.</param>
    /// <returns>The verdict; a refused connection is <see cref="MemexReadinessState.NotYet"/>.</returns>
    public static async Task<MemexReadinessVerdict> ProbeAsync(HttpClient http, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        try
        {
            using var mesh = await http.GetAsync(MeshPath, ct).ConfigureAwait(false);
            using var health = await http.GetAsync(HealthPath, ct).ConfigureAwait(false);
            var body = await health.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Classify((int)mesh.StatusCode, mesh.Headers.Location?.ToString(), (int)health.StatusCode, body);
        }
        catch (HttpRequestException ex)
        {
            // The listener is not up yet. Keep waiting — the Testcontainers start budget bounds it.
            return new(MemexReadinessState.NotYet, $"no HTTP answer yet: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The client's own per-request timeout: a booting host that accepted the socket but has
            // not answered its census yet. The caller's cancellation is NOT swallowed (the filter).
            return new(MemexReadinessState.NotYet, $"no HTTP answer within the request timeout: {ex.Message}");
        }
    }

    private static bool IsSetupLocation(string? location)
    {
        if (string.IsNullOrEmpty(location)) return false;
        var path = Uri.TryCreate(location, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : location.Split('?', 2)[0];
        return path.Equals(SetupPath, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(SetupPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Clip(string s) => s.Length <= 80 ? s : s[..80] + "…";
}

/// <summary>What a memex's HTTP surface says about its mesh.</summary>
public enum MemexReadinessState
{
    /// <summary>No startup census of a composed mesh yet — keep waiting.</summary>
    NotYet,

    /// <summary>The composed host answered its startup census Healthy or Degraded.</summary>
    MeshStarted,

    /// <summary>The setup-only host: no mesh, and none will come without a person. Terminal.</summary>
    SetupWizard,
}

/// <summary>A readiness verdict and the reading behind it.</summary>
/// <param name="State">The state.</param>
/// <param name="Reason">What was read, for the test's output or the exception.</param>
public sealed record MemexReadinessVerdict(MemexReadinessState State, string Reason);

/// <summary>
/// The wait strategy <see cref="MemexBuilder"/> installs: satisfied only by a started mesh, and
/// FAILING at once — never polling to the timeout — when the container is the setup wizard.
/// </summary>
internal sealed class UntilMeshStarted : IWaitUntil
{
    public async Task<bool> UntilAsync(IContainer container)
    {
        // A startup gate that refuses (DbVersionGate over an unmigrated schema, an unwritable data
        // root) EXITS the process; nothing will ever answer, so say so now rather than at the
        // start budget. The container's own log, redirected to the test output, names the gate.
        if (container.State is TestcontainersStates.Exited or TestcontainersStates.Dead)
            throw new InvalidOperationException(
                $"the memex container {container.State} before its mesh started (exit code "
                + $"{await container.GetExitCodeAsync().ConfigureAwait(false)}). Read its log above: a "
                + "'DbVersionGate' line means the database was not migrated (run the memex-migration "
                + "image of the same build first).");

        var baseAddress = new UriBuilder(Uri.UriSchemeHttp, container.Hostname,
            container.GetMappedPublicPort(MemexBuilder.HttpPort)).Uri;
        var verdict = await MemexReadiness.ProbeAsync(baseAddress).ConfigureAwait(false);
        return verdict.State switch
        {
            MemexReadinessState.MeshStarted => true,
            MemexReadinessState.SetupWizard => throw new InvalidOperationException(verdict.Reason),
            _ => false,
        };
    }
}
