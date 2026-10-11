using System.Collections.Immutable;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;

namespace MeshWeaver.PluginTester;

/// <summary>
/// <c>mw-plugin-test module-identity &lt;bundle&gt;… --expect &lt;identity&gt;</c>: does every module
/// bundle state that it was built for <c>&lt;identity&gt;</c>? (#3583, policy
/// <c>bake-per-live-identity</c>.)
///
/// <para><b>Why a verb, not a shell compare.</b> Across a declared compatibility break the
/// publish-bake lane's <c>previous</c> leg CARRIES FORWARD the previous epoch's sealed module set
/// for the previous-epoch identity, instead of building modules a second time. Before it seals
/// that set it must refuse any bundle that was not built for the declared
/// <c>previousEpochCeiling</c>'s identity. "Built for" is
/// <see cref="ModuleFrameworkIdentity.Compare"/>'s rule: scheme-aware, so a compatibility key, a
/// surface hash and a commit token are never compared as strings. A second copy of that rule in
/// shell would be a second rule.</para>
///
/// <para><b>Strict on purpose.</b> Only <see cref="ModuleIdentityVerdict.Matches"/> passes. A
/// bundle that states nothing, or states an identity in a scheme the expected one cannot be
/// compared with, is REFUSED: the lane cannot show that those bytes belong to the old epoch, and
/// sealing them under its key would be sealing a guess.</para>
/// </summary>
public static class ModuleIdentityCheck
{
    /// <summary>The verb's name on the command line.</summary>
    public const string Verb = "module-identity";

    /// <summary>The usage line.</summary>
    public const string Usage =
        "usage: mw-plugin-test module-identity <bundle.module.nupkg>... --expect <identity>\n"
        + "  Reads each module bundle's recorded framework identity and compares it with <identity> "
        + "by ModuleFrameworkIdentity's scheme-aware rule. Exit 0 when EVERY bundle matches, 1 when any "
        + "does not (each one named with its verdict), 2 on bad usage or an unreadable bundle.";

    /// <summary>One bundle's reading.</summary>
    /// <param name="Bundle">The bundle's file name.</param>
    /// <param name="Stated">The framework identity its manifest records, or null.</param>
    /// <param name="Match">The comparison against the expected identity.</param>
    public sealed record Reading(string Bundle, string? Stated, ModuleIdentityMatch Match)
    {
        /// <summary>Only a measured match passes.</summary>
        public bool Accepted => Match.Verdict == ModuleIdentityVerdict.Matches;
    }

    /// <summary>
    /// Reads one bundle's MANIFEST and compares the identity it states with <paramref name="expected"/>.
    /// Only the manifest entry is read (<see cref="BundleReader.ReadManifest(string)"/>, streamed off
    /// disk) — the check needs the recorded framework identity and nothing else, so no assembly is
    /// decompressed and the bundle's bytes never sit in the managed heap.
    /// </summary>
    /// <param name="bundlePath">The bundle on disk.</param>
    /// <param name="expected">The identity the bundle must have been built for.</param>
    /// <returns>The reading.</returns>
    /// <exception cref="InvalidDataException">The archive carries no module manifest, or is not an archive.</exception>
    /// <exception cref="System.Text.Json.JsonException">The manifest is not valid JSON.</exception>
    public static Reading Check(string bundlePath, string expected)
    {
        var name = Path.GetFileName(bundlePath);
        var manifest = BundleReader.ReadManifest(bundlePath)
            ?? throw new InvalidDataException($"'{name}' carries no module manifest — not a module bundle");
        return new Reading(name, manifest.FrameworkMvid,
            ModuleFrameworkIdentity.Compare(manifest.FrameworkMvid, [expected]));
    }

    /// <summary>Runs the verb.</summary>
    /// <param name="args">The arguments after the verb.</param>
    /// <param name="output">Where each reading goes.</param>
    /// <param name="error">Where usage errors and refusals go.</param>
    /// <returns>0 every bundle matches, 1 any does not, 2 bad usage or an unreadable bundle.</returns>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? expected = null;
        var bundles = ImmutableList.CreateBuilder<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--expect" when i + 1 < args.Length:
                    expected = args[++i].Trim();
                    break;
                case "--expect":
                    error.WriteLine("module-identity: --expect requires a value.");
                    return 2;
                case "--help" or "-h":
                    output.WriteLine(Usage);
                    return 0;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        error.WriteLine($"module-identity: unknown option '{args[i]}'.");
                        error.WriteLine(Usage);
                        return 2;
                    }
                    bundles.Add(args[i]);
                    break;
            }
        }
        if (string.IsNullOrEmpty(expected) || bundles.Count == 0)
        {
            error.WriteLine("module-identity: --expect <identity> and at least one bundle are required. "
                + "A check over no bundles verifies nothing, so it is refused rather than passed.");
            error.WriteLine(Usage);
            return 2;
        }

        var refused = 0;
        foreach (var path in bundles)
        {
            Reading reading;
            try
            {
                reading = Check(path, expected);
            }
            // An unreadable bundle — missing, not an archive, no manifest, or a manifest that is
            // not JSON — is exit 2 as documented, never an unhandled throw the CLI reports as 70.
            catch (Exception ex) when (ex is IOException or InvalidDataException
                                           or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                error.WriteLine($"module-identity: cannot read '{path}' — {ex.Message}");
                return 2;
            }
            if (reading.Accepted)
                output.WriteLine($"module-identity: MATCH {reading.Bundle} — states {reading.Stated}, built for {expected}");
            else
            {
                refused++;
                error.WriteLine($"module-identity: REFUSED {reading.Bundle} — {reading.Match.Describe(reading.Stated)}; "
                    + $"expected a bundle built for {expected}");
            }
        }
        output.WriteLine($"module-identity: {bundles.Count - refused} of {bundles.Count} bundle(s) built for {expected}");
        return refused == 0 ? 0 : 1;
    }
}
