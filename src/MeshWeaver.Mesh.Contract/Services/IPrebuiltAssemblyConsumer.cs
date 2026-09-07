using System;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Consumes pre-built NodeType assemblies for a SPECIFIC set of types, at the moment their
/// content lands (#1707 slice 3: "any type could have a pre-compiled lib — if yes, we take it;
/// if no, we generate"). Implemented over the deployment's bundle sources (the image's shipped
/// bundles and the CI-published, framework-identity-keyed root) by the hosting layer; consumed by
/// the install orchestrator (a package's written NodeTypes) and the git-sync push path (a
/// commit's affected NodeTypes) — the two flows where content arrives together with the
/// knowledge of exactly which types it brought, so adoption can run BEFORE the release pipeline
/// would otherwise compile.
///
/// <para>Adoption is validated per assembly (framework identity + per-type dependency record) and
/// stamps the full compile write-back, so an adopted type is indistinguishable from a locally
/// compiled one. Cold; emits the number of assemblies adopted; NEVER faults — every failure
/// degrades to "the release pipeline compiles, as it would have anyway". Resolve optionally
/// (<c>GetService</c>): a host without bundle consumption simply has no registration, and the
/// caller proceeds straight to compiling.</para>
/// </summary>
public interface IPrebuiltAssemblyConsumer
{
    /// <summary>Attempts to adopt pre-built assemblies for exactly <paramref name="typePaths"/>;
    /// emits the adopted count (0 when nothing matched or nothing is configured).</summary>
    IObservable<int> SeedForTypes(IReadOnlyCollection<string> typePaths);

    /// <summary>
    /// <see cref="SeedForTypes(IReadOnlyCollection{string})"/> with a per-path witness:
    /// <paramref name="onOffered"/> is invoked once for every requested path a bundle entry NAMED
    /// — adopted now, already current, or DECLINED — from the consumer's own worker threads, so an
    /// implementation must be thread-safe. The count is unchanged and still authoritative; the
    /// witness answers the one question the count cannot: "was there a bundle for this type at
    /// all?" — which is what separates "the bake has not landed for this identity" from "the bake
    /// is here and was refused" when the compile watcher decides whether a local compile is honest
    /// (<c>IPartitionSourceTracking</c>, MeshWeaver#3583).
    ///
    /// <para>Defaulted so that an implementation predating the witness keeps its contract; it then
    /// reads as "nothing offered", which degrades in the direction the gate tolerates — a compile,
    /// never a park.</para>
    /// </summary>
    IObservable<int> SeedForTypes(IReadOnlyCollection<string> typePaths, Action<string> onOffered)
        => SeedForTypes(typePaths);
}
