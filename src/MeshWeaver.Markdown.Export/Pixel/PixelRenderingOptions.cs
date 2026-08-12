using System.Collections.Immutable;

namespace MeshWeaver.Markdown.Export.Pixel;

/// <summary>
/// Deployment-level settings for the headless browser that prints every PDF export — both
/// fidelities (<see cref="Configuration.ExportFidelity"/>).
///
/// <para><b>The <c>portal-ai</c> image ships the browser</b> (<c>deploy/base-images/portal-ai</c>:
/// a Playwright headless-shell Chromium at <c>/usr/bin/chromium</c>, with <c>CHROME_BIN</c> set).
/// It has to, since #1230 made the browser the renderer for every PDF rather than an opt-in extra
/// for design-led decks. The lean <c>portal</c> flavour uses the stock ASP.NET base and carries no
/// browser.</para>
///
/// <para>Resolution stays runtime and overridable, so a deployment can point at a different
/// binary: <see cref="ExecutablePath"/>, then the well-known environment variables, then the
/// platform's usual install locations. When nothing is found, pixel fidelity is not offered and a
/// PDF export fails loudly rather than returning a file that quietly lost its formatting.</para>
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
