using MeshWeaver.Compiler;

namespace MeshWeaver.PluginTester;

/// <summary>
/// The gate's <c>--app &lt;dir&gt;</c> precondition: <b>this process must BE the platform host it is
/// asked to judge for.</b>
///
/// <para>A bake keyed to the platform's identity (<see cref="BakeHost"/>) is adopted through the
/// consumer's own environment — <c>PrebuiltAssemblySeeder</c> compares the bundle's identity with
/// the process's, and the per-type dependency record with the process's manifest pairs and MVIDs.
/// Those are process facts (<c>AppContext.BaseDirectory</c>), and the gate deliberately runs the
/// SAME consumption implementation a portal runs rather than a gate-only reader. So the only way a
/// gate can judge the platform's bake is to run AS the platform: the platform image's <c>/app</c>
/// with this CLI laid beside it (<c>compose-gate-host.sh</c>), started from the platform image's
/// runtime. Then the manifest is the platform's, the MVIDs are the platform's, every
/// platform-shipped assembly (<c>MeshWeaver.Maps</c>…) is in the TPA and LOADS, and adoption is
/// decided exactly as a portal would decide it.</para>
///
/// <para>🚨 A gate running as a DIFFERENT host would not fail — it would decline every bundle the
/// bake addressed to the platform, compile the whole tree itself and exit green having judged none
/// of the bytes that ship (the #1814 shape one level down). <see cref="Verify"/> therefore refuses
/// before the mesh boots, naming both identities, when the process does not resolve the identity
/// of the directory it was told is the platform.</para>
/// </summary>
internal static class GateHostCheck
{
    /// <summary>
    /// The problem with running this process as the gate for the platform host at
    /// <paramref name="appDirectory"/>, or null when the process resolves that host's identity.
    /// </summary>
    /// <param name="appDirectory">The platform host's application directory (a portal image's <c>/app</c>).</param>
    /// <param name="liveIdentity">The identity THIS process resolves (<c>PrebuiltAssemblySeeder.LiveFrameworkMvid</c>).</param>
    public static string? Verify(string appDirectory, string liveIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(liveIdentity);
        var app = Path.GetFullPath(appDirectory);
        var (identity, problem) = FrameworkBuildIdentity.ResolveIdentityForDirectory(app);
        if (identity is null)
            return $"'{app}' resolves no framework identity — {problem}. It is not a platform host "
                + "this gate can run as.";
        if (string.Equals(identity, liveIdentity, StringComparison.Ordinal))
            return null;
        return $"this gate process resolves framework identity '{liveIdentity}' but the platform host "
            + $"at '{app}' resolves '{identity}'. The gate must RUN AS the platform host — the host's own "
            + "/app with this CLI laid beside it (compose-gate-host.sh), started from the platform "
            + "image — so the mesh it stands up loads, records and adopts exactly what a portal does. "
            + "Running as a different host, it would decline every bundle the bake addressed to the "
            + "platform, compile the tree itself and pass without judging the bytes that ship.";
    }
}
