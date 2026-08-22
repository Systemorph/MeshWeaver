#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// 🚨 THE LOAD-BEARING CLAIM of #1763: <b>the bake produces its artifacts without standing up a
/// mesh.</b> Asserted STRUCTURALLY — by walking the IL call graph of the shipped binary — not by
/// timing, and not by grepping the sources.
///
/// <para><b>Why not timing.</b> "The bake got faster" is evidence of nothing: a mesh that boots
/// quickly on a warm machine looks exactly like no mesh at all, and the failure this guards against
/// (someone reaching for a hub from the build path because it was convenient) would arrive as a few
/// hundred milliseconds nobody notices.</para>
///
/// <para><b>Why not a grep.</b> A grep over <c>TreeBake.cs</c> proves only that THAT FILE does not
/// say <c>MeshBuilder</c>. The interesting regression is a helper three calls down that does. This
/// walks the transitive call graph from <see cref="TreeBake.Run"/> through every method the tool
/// assembly defines, and fails if any of them so much as NAMES a mesh-construction type or entry
/// point.</para>
///
/// <para><b>The control is the point.</b> The identical walk from
/// <see cref="PluginGateRunner.Run"/> — the GATE, which legitimately builds a mesh because
/// rendering an area and executing a <c>Tests</c> area are runtime behaviours — must find every one
/// of those markers. Without it a walk that silently reached nothing would "prove" the bake
/// mesh-free while proving only that the walker was broken.</para>
///
/// <para><b>What the walk sees, and what it does not.</b> Every type and member the IL NAMES, in
/// every method defined by <c>mw-plugin-test</c> that is transitively reachable from the seed —
/// including compiler-generated lambdas and display classes, which are reached through their
/// <c>ldftn</c> / <c>newobj</c> tokens. It does NOT decode signature blobs, so a type appearing
/// only as a generic argument inside a <c>MethodSpec</c> is invisible; the marker set below is
/// therefore anchored on construction and entry-point names, which necessarily appear as tokens.
/// It also stops at the assembly boundary: what <c>MeshWeaver.Compiler</c> does internally is that
/// assembly's business, and it is a library with no hub dependency by construction (#1712).</para>
/// </summary>
public class MeshFreeBakePathTest(ITestOutputHelper output)
{
    /// <summary>
    /// Names whose appearance anywhere on a call path means a mesh is being built or driven: the
    /// builder itself, the hub abstraction, and the composition entry points
    /// <c>PluginGateRunner.GateMesh.Create</c> uses. Matched on the simple name — the IL names the
    /// declaring type of every member it calls.
    /// </summary>
    private static readonly ImmutableSortedSet<string> MeshConstructionMarkers =
        ImmutableSortedSet.Create(StringComparer.Ordinal,
            "MeshBuilder",
            "IMessageHub",
            "AddGraph",
            "UseMonolithMesh",
            "AddInMemoryPersistence",
            "CreateMeshWeaverServiceProvider");

    [Fact(Timeout = 120_000)]
    public void TheCompilerDrivenBake_BuildsNoMesh_WhileTheGateDoes()
    {
        var assemblyPath = typeof(TreeBake).Assembly.Location;
        Assert.True(File.Exists(assemblyPath), $"cannot read the tool assembly at '{assemblyPath}'");

        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var graph = new CallGraph(pe, reader);

        // ── THE CONTROL FIRST. If the walker cannot find a mesh where one is built, nothing it
        //    says about the bake means anything.
        var gate = graph.NamesReachableFrom("MeshWeaver.PluginTester", "PluginGateRunner", "Run");
        output.WriteLine(
            $"gate: {gate.Methods} method(s) walked, {gate.Names.Count} distinct name(s) referenced");
        var foundInGate = MeshConstructionMarkers.Intersect(gate.Names);
        Assert.True(
            foundInGate.SetEquals(MeshConstructionMarkers),
            "the CONTROL failed: walking PluginGateRunner.Run — which demonstrably builds a mesh — "
            + "did not reach "
            + string.Join(", ", MeshConstructionMarkers.Except(foundInGate))
            + ". The walker is broken, so its verdict on the bake path proves nothing.");

        // ── THE CLAIM. The same walk from the bake's entry point must reach none of them.
        var bake = graph.NamesReachableFrom("MeshWeaver.PluginTester", "TreeBake", "Run");
        output.WriteLine(
            $"bake: {bake.Methods} method(s) walked, {bake.Names.Count} distinct name(s) referenced");
        Assert.True(bake.Methods > 1, "the bake walk reached nothing — the seed method was not found");
        var foundInBake = MeshConstructionMarkers.Intersect(bake.Names);
        Assert.True(
            foundInBake.Count == 0,
            "TreeBake.Run reaches mesh construction: " + string.Join(", ", foundInBake)
            + ". Producing an assembly is a BUILD step (#1763) — the mesh's job is to CONSUME a "
            + "bake, not to produce one. Whatever needed a hub belongs behind the gate.");
    }

    /// <summary>The transitive IL call graph of one assembly, walked from a named method.</summary>
    private sealed class CallGraph(PEReader pe, MetadataReader reader)
    {
        /// <param name="Methods">How many method bodies the walk decoded.</param>
        /// <param name="Names">Every type and member simple name the IL of those bodies referenced.</param>
        internal sealed record Reach(int Methods, ImmutableSortedSet<string> Names);

        internal Reach NamesReachableFrom(string @namespace, string typeName, string methodName)
        {
            var seed = FindMethod(@namespace, typeName, methodName);
            var seen = new HashSet<MethodDefinitionHandle>();
            var queue = new Queue<MethodDefinitionHandle>();
            var names = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
            queue.Enqueue(seed);
            seen.Add(seed);
            var walked = 0;

            while (queue.Count > 0)
            {
                var handle = queue.Dequeue();
                var method = reader.GetMethodDefinition(handle);
                names.Add(reader.GetString(method.Name));
                if (method.RelativeVirtualAddress == 0)
                    continue; // abstract / extern / interface — no body to walk
                walked++;
                foreach (var token in TokensOf(pe.GetMethodBody(method.RelativeVirtualAddress)))
                {
                    foreach (var name in NamesOf(token))
                        names.Add(name);
                    if (token.Kind == HandleKind.MethodDefinition
                        && seen.Add((MethodDefinitionHandle)token))
                        queue.Enqueue((MethodDefinitionHandle)token);
                }
            }
            return new Reach(walked, names.ToImmutable());
        }

        private MethodDefinitionHandle FindMethod(string @namespace, string typeName, string methodName)
        {
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                if (reader.GetString(type.Namespace) != @namespace
                    || reader.GetString(type.Name) != typeName)
                    continue;
                foreach (var methodHandle in type.GetMethods())
                    if (reader.GetString(reader.GetMethodDefinition(methodHandle).Name) == methodName)
                        return methodHandle;
            }
            throw new InvalidOperationException(
                $"{@namespace}.{typeName}.{methodName} is not in the assembly — the test's seed is "
                + "stale, which would make its verdict meaningless.");
        }

        /// <summary>The simple names a metadata token contributes: the member's own name and its
        /// declaring type's.</summary>
        private IEnumerable<string> NamesOf(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.MethodDefinition:
                {
                    var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                    yield return reader.GetString(method.Name);
                    yield return reader.GetString(
                        reader.GetTypeDefinition(method.GetDeclaringType()).Name);
                    break;
                }
                case HandleKind.FieldDefinition:
                {
                    var field = reader.GetFieldDefinition((FieldDefinitionHandle)handle);
                    yield return reader.GetString(field.Name);
                    yield return reader.GetString(
                        reader.GetTypeDefinition(field.GetDeclaringType()).Name);
                    break;
                }
                case HandleKind.MemberReference:
                {
                    var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                    yield return reader.GetString(member.Name);
                    foreach (var parent in NamesOf(member.Parent))
                        yield return parent;
                    break;
                }
                case HandleKind.MethodSpecification:
                {
                    var spec = reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                    foreach (var name in NamesOf(spec.Method))
                        yield return name;
                    break;
                }
                case HandleKind.TypeReference:
                    yield return reader.GetString(
                        reader.GetTypeReference((TypeReferenceHandle)handle).Name);
                    break;
                case HandleKind.TypeDefinition:
                    yield return reader.GetString(
                        reader.GetTypeDefinition((TypeDefinitionHandle)handle).Name);
                    break;
                default:
                    // TypeSpecification (a generic instantiation) and StandaloneSignature carry
                    // their content in a signature blob this walk deliberately does not decode —
                    // see the class remarks on what that costs.
                    break;
            }
        }

        /// <summary>
        /// Every metadata token a method body's IL references. A full opcode walk, not a byte scan:
        /// operand LENGTHS come from the BCL's own opcode table, so a four-byte integer literal can
        /// never be mistaken for a token (which would make this test fail at random).
        /// </summary>
        private static IEnumerable<EntityHandle> TokensOf(MethodBodyBlock body)
        {
            var il = body.GetILContent();
            var position = 0;
            while (position < il.Length)
            {
                var first = il[position];
                ushort code;
                if (first == 0xFE && position + 1 < il.Length)
                {
                    code = (ushort)(0xFE00 | il[position + 1]);
                    position += 2;
                }
                else
                {
                    code = first;
                    position += 1;
                }
                if (!OpCodeTable.TryGetValue(code, out var operand))
                    yield break; // an opcode the table does not know: stop rather than mis-decode
                switch (operand)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        position += 1;
                        break;
                    case OperandType.InlineVar:
                        position += 2;
                        break;
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                    {
                        if (position + 4 > il.Length)
                            yield break;
                        var token = BitConverter.ToInt32([.. il.Skip(position).Take(4)], 0);
                        position += 4;
                        if (token != 0)
                        {
                            var handle = MetadataTokens.EntityHandle(token);
                            if (!handle.IsNil)
                                yield return handle;
                        }
                        break;
                    }
                    case OperandType.InlineBrTarget:
                    case OperandType.InlineI:
                    case OperandType.InlineSig:
                    case OperandType.InlineString:
                    case OperandType.ShortInlineR:
                        position += 4;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        position += 8;
                        break;
                    case OperandType.InlineSwitch:
                    {
                        if (position + 4 > il.Length)
                            yield break;
                        var count = BitConverter.ToInt32([.. il.Skip(position).Take(4)], 0);
                        position += 4 + (4 * count);
                        break;
                    }
                    default:
                        yield break;
                }
            }
        }

        /// <summary>
        /// Opcode → operand kind, read off <see cref="OpCodes"/> itself. Hand-maintaining this
        /// table is how an IL walker acquires a silent blind spot; the BCL already has it.
        /// </summary>
        private static readonly IReadOnlyDictionary<ushort, OperandType> OpCodeTable =
            typeof(OpCodes)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(OpCode))
                .Select(f => (OpCode)f.GetValue(null)!)
                .ToDictionary(op => (ushort)op.Value, op => op.OperandType);
    }
}
