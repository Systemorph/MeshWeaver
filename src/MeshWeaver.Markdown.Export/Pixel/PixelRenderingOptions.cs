using System.Collections.Immutable;

namespace MeshWeaver.Markdown.Export.Pixel;

/// <summary>
/// Deployment-level settings for pixel-faithful export (<see cref="Configuration.ExportFidelity.Pixel"/>).
///
/// <para><b>The browser is NOT shipped in the portal image.</b> MeshWeaver ships the *capability*;
/// the binary is an operator decision, because putting a browser in the shared portal image costs
/// every tenant image size, start-up surface and a sandbox/security question that most decks never
/// need. So the renderer resolves an <b>already-installed</b> Chromium/Chrome at runtime — from
/// <see cref="ExecutablePath"/>, then from the well-known environment variables, then from the
/// platform's usual install locations. When nothing is found, pixel fidelity simply is not offered
/// and the content-faithful export is unaffected.</para>
/// </summary>
public record PixelRenderingOptions
{
    /// <summary>
    /// Explicit path to a Chromium / Chrome / Edge executable. Highest precedence. When null the
    /// renderer falls back to <see cref="EnvironmentVariables"/> and then to the platform's
    /// well-known install locations.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Pass <c>--no-sandbox</c> to the browser. Off by default deliberately: it disables Chromium's
    /// process sandbox and is a security decision the operator must make, not a default we make for
    /// them. Most containers running as a non-root user without <c>SYS_ADMIN</c> need it (or a
    /// seccomp profile that permits user namespaces) for the browser to start at all.
    /// </summary>
    public bool NoSandbox { get; init; }

    /// <summary>
    /// How long the browser may take to lay out and print one document before it is killed and the
    /// export fails. Bounds a wedged or crash-looping browser so it cannot hold an I/O pool slot.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long the browser lets the page settle (fonts, images, web-font swaps, layout) before it
    /// prints, as Chromium's <c>--virtual-time-budget</c>. Too short and a webfont or background
    /// image can miss the print; too long and every export pays for it.
    /// </summary>
    public TimeSpan SettleBudget { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Extra command-line arguments appended verbatim (escape hatch for odd images).</summary>
    public ImmutableList<string> AdditionalArguments { get; init; } = [];

    /// <summary>
    /// Environment variables consulted, in order, when <see cref="ExecutablePath"/> is null.
    /// <c>CHROME_BIN</c> and <c>PUPPETEER_EXECUTABLE_PATH</c> are the two conventions container
    /// images that already carry a browser tend to set, so an image that has one works with no
    /// MeshWeaver configuration at all.
    /// </summary>
    public static readonly ImmutableArray<string> EnvironmentVariables =
    [
        "MESHWEAVER_CHROMIUM_PATH",
        "CHROME_BIN",
        "PUPPETEER_EXECUTABLE_PATH"
    ];

    /// <summary>
    /// Well-known install locations probed last, per platform. Immutable constant lookup — never
    /// written at runtime.
    /// </summary>
    public static readonly ImmutableArray<string> WellKnownPaths =
    [
        // Linux (Debian/Ubuntu/Alpine package names all covered)
        "/usr/bin/chromium",
        "/usr/bin/chromium-browser",
        "/usr/bin/google-chrome",
        "/usr/bin/google-chrome-stable",
        "/usr/bin/microsoft-edge",
        "/snap/bin/chromium",
        // macOS
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        "/Applications/Chromium.app/Contents/MacOS/Chromium",
        "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
        // Windows
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
    ];
}
