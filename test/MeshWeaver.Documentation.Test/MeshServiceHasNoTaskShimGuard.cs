using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance ratchet on the <c>Task</c>-shaped surface of <see cref="IMeshService"/>.
///
/// <para>The mesh is turn-based. An <c>await</c> on a hub action block, a grain turn or a Blazor
/// circuit parks the single-threaded scheduler that has to process the very message being waited
/// on — so a <c>Task</c>-returning CRUD verb is not a convenience, it is a deadlock one autocomplete
/// away from every handler. Two assertions, because the two halves have different futures.</para>
///
/// <para><b>The interface itself must stay clean, permanently.</b> <see cref="IMeshService"/>
/// declares only cold <see cref="IObservable{T}"/> verbs, and that is not negotiable.</para>
///
/// <para><b>The extension shim is DEBT with a measured blocker, so it is ratcheted rather than
/// banned.</b> <c>MeshServiceExtensions</c> (<c>CreateNodeAsync</c>/<c>UpdateNodeAsync</c>/
/// <c>DeleteNodeAsync</c> over a <see cref="TaskCompletionSource{TResult}"/>) still ships on
/// <c>MeshWeaver.Mesh.Contract</c>. Its own doc comment claims "~180 existing callers"; measured
/// 2026-08-27 that is <b>zero</b> in <c>src/</c>, <c>samples/</c>, <c>memex/</c> and
/// <c>content/</c> — every caller left in this repo is a test, and the move to
/// <c>MeshWeaver.Fixture</c> was drafted and then withdrawn, because the callers that matter are
/// somewhere no compiler here can look: <b>MeshWeaver.Reinsurance has 58 call sites across 22
/// in-mesh <c>Source/*.cs</c> files</b>. In-mesh source compiles at RUNTIME in the portal, so
/// deleting the shim turns 22 NodeTypes <c>CompileError</c> — and a <c>CompileError</c> NodeType
/// refuses portal readiness. Green CI would have proved nothing.</para>
///
/// <para>So the exit is two steps, in order: port those 58 sites to
/// <c>CreateNode(...).Subscribe(...)</c> (a real fix — each is an <c>await</c> on hub-reachable
/// layout-area code), THEN move the shim beside the test-only <c>QueryAsync</c> bridge in
/// <c>MeshWeaver.Fixture</c> (tracked as MeshWeaver.Reinsurance issue #102). Until then this test
/// holds the line where it is: exactly one shipped
/// assembly, exactly the three methods below. <b>A fourth method, or a second assembly, fails
/// here</b> — the shim may shrink, never grow. When it finally moves, delete
/// <see cref="ExpectedShimAssembly"/> and flip the assertion to "none".</para>
/// </summary>
public class MeshServiceHasNoTaskShimGuard(ITestOutputHelper output)
{
    /// <summary>The one shipped assembly allowed to carry the bridge, until the Reinsurance port lands.</summary>
    private const string ExpectedShimAssembly = "MeshWeaver.Mesh.Contract";

    /// <summary>The seeded inventory. It may shrink; a NEW name here is the thing being prevented.</summary>
    private static readonly string[] ExpectedShimMethods =
        ["CreateNodeAsync", "DeleteNodeAsync", "UpdateNodeAsync"];

    private static bool IsTaskShaped(Type t) =>
        typeof(Task).IsAssignableFrom(t)
        || t == typeof(ValueTask)
        || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ValueTask<>));

    /// <summary>The interface declares only cold <see cref="IObservable{T}"/> verbs.</summary>
    [Fact]
    public void IMeshService_DeclaresNoTaskReturningMember()
    {
        var offenders = typeof(IMeshService).GetMethods()
            .Where(m => IsTaskShaped(m.ReturnType))
            .Select(m => $"{m.ReturnType.Name} {m.Name}(...)")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "IMeshService must expose only IObservable<T> verbs — a Task-returning member on it "
            + "deadlocks any hub handler that awaits it. Found: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The extension shim stays exactly where and what it is.
    ///
    /// <para>The scan loads every <c>MeshWeaver.*.dll</c> sitting beside this test rather than
    /// walking <c>AppDomain.CurrentDomain.GetAssemblies()</c>. That distinction is the difference
    /// between a guard and a decoration: .NET loads an assembly on first use, so a live-domain walk
    /// inspects whatever this test run happened to touch — a shim added to a project nothing in
    /// this fixture references would not be there to find, and the guard would pass having looked
    /// at nothing. It also asserts it found a populated set, and that it found the KNOWN shim,
    /// before trusting any zero.</para>
    /// </summary>
    [Fact]
    public void The_Task_shim_stays_one_assembly_and_three_methods()
    {
        var dir = AppContext.BaseDirectory;
        var assemblies = Directory.EnumerateFiles(dir, "MeshWeaver.*.dll")
            .Select(TryLoad)
            .Where(a => a is not null)
            .Select(a => a!)
            .ToArray();

        // Positive control: the scan must be looking at a real, populated set.
        Assert.True(assemblies.Length >= 10,
            $"Only {assemblies.Length} MeshWeaver assemblies loaded from {dir} — the assertions "
            + "below would be reporting a clean tree they never looked at.");

        var hits = assemblies.SelectMany(FindTaskReturningMeshServiceExtensions).ToArray();
        foreach (var h in hits)
            output.WriteLine($"{h.Assembly}: {h.Type}.{h.Method}");

        var assembliesCarryingIt = hits.Select(h => h.Assembly).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToArray();
        Assert.True(
            assembliesCarryingIt.SequenceEqual([ExpectedShimAssembly]),
            "The Task-returning IMeshService bridge must live in exactly one shipped assembly "
            + $"({ExpectedShimAssembly}) until the 58 in-mesh MeshWeaver.Reinsurance call sites are "
            + "ported to CreateNode(...).Subscribe(...); then it moves to MeshWeaver.Fixture and "
            + "this expectation becomes 'none'. A SECOND assembly is new debt, not a move. Found: ["
            + string.Join(", ", assembliesCarryingIt) + "]");

        var methods = hits.Select(h => h.Method).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToArray();
        Assert.True(
            methods.SequenceEqual(ExpectedShimMethods),
            "The bridge may SHRINK, never grow — a new *Async verb here is a new deadlock waiting "
            + "for a hub handler to call it. Compose the observable and Subscribe instead. Expected ["
            + string.Join(", ", ExpectedShimMethods) + "], found [" + string.Join(", ", methods) + "]");
    }

    private static IEnumerable<(string Assembly, string Type, string Method)>
        FindTaskReturningMeshServiceExtensions(Assembly a) =>
        SafeTypes(a)
            .Where(t => t is { IsAbstract: true, IsSealed: true, IsPublic: true })   // static class
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.IsDefined(typeof(ExtensionAttribute), false))
            .Where(m => IsTaskShaped(m.ReturnType))
            .Where(m => m.GetParameters().FirstOrDefault()?.ParameterType == typeof(IMeshService))
            .Select(m => (a.GetName().Name ?? "(unknown)", m.DeclaringType!.FullName ?? "(unknown)", m.Name));

    private static Assembly? TryLoad(string path)
    {
        try { return Assembly.LoadFrom(path); }
        catch (BadImageFormatException) { return null; }   // native / mixed-mode neighbour
        catch (FileLoadException) { return null; }
    }

    private static Type[] SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null).ToArray()!; }
    }
}
