using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Text;

namespace MeshWeaver.Mesh;

/// <summary>
/// What <see cref="ModulePlatformLink"/> measures BEYOND its type and version halves, and over
/// which assemblies. The default (<see cref="TypesOnly"/>) is what every runtime call site uses —
/// the boot probe, the landing, prebuilt adoption and the roll gate — and is byte-for-byte the
/// verdict those sites always received.
/// </summary>
public sealed record ModuleLinkOptions
{
    /// <summary>The runtime default: types and assembly versions, no member walk.</summary>
    public static ModuleLinkOptions TypesOnly { get; } = new();

    /// <summary>The static compatibility gate's setting: every member reference into a platform
    /// assembly is resolved by NAME and SIGNATURE as well.</summary>
    public static ModuleLinkOptions WithMembers { get; } = new() { CheckMembers = true };

    /// <summary>
    /// 🚨 Resolve every MEMBER a module calls — methods, constructors, property/event accessors,
    /// fields — against the platform type's definition, by name and exact signature. This is the
    /// half of platform backwards compatibility no type-level check can see: a method removed or
    /// re-signed on a type that stayed loads cleanly and throws <c>MissingMethodException</c> at
    /// the first call (policy <c>platform-backwards-compatibility</c>). Needs a file-backed surface
    /// (<see cref="ModulePlatformSurface.OfFiles"/> / <see cref="ModulePlatformSurface.OfRunningProcess"/>);
    /// against a published surface document — which describes type names only — the verdict is
    /// <see cref="ModuleLinkState.Indeterminate"/>, never a silent type-level pass.
    ///
    /// <para>Member checking covers the PLATFORM's own assemblies (<c>MeshWeaver.*</c>). The base
    /// class library is outside it: its compatibility is the runtime's contract, and third-party
    /// drift is what the version half already measures.</para>
    /// </summary>
    public bool CheckMembers { get; init; }

    /// <summary>
    /// The assemblies this measurement has AUTHORITY over, or null for every assembly the surface
    /// carries. A core pull request builds the core platform only; a module's references into an
    /// assembly the PORTAL host ships from another repository cannot be moved by that pull request
    /// and are reported unchecked rather than refused as "absent". Compared by simple name,
    /// case-insensitively.
    /// </summary>
    public IReadOnlySet<string>? JudgedAssemblies { get; init; }

    /// <summary>Whether a reference into <paramref name="assemblyName"/> is in scope.</summary>
    internal bool Judges(string assemblyName) =>
        JudgedAssemblies is null || JudgedAssemblies.Contains(assemblyName);
}

public static partial class ModulePlatformLink
{
    /// <summary>The member half's running tally, one per check.</summary>
    private sealed class MemberTally
    {
        public List<string> Missing { get; } = [];
        public List<string> Unverified { get; } = [];
        public int Checked { get; set; }
    }

    /// <summary>
    /// Walks every <c>MemberRef</c> of the module whose parent is a type in a judged platform
    /// assembly and resolves it against that type's definition — directly, then along its base
    /// types and interfaces within the surface. Returns false when a judged platform assembly
    /// carries no member signatures (a declared surface): the verdict must then be Indeterminate.
    /// </summary>
    private static bool MeasureMembers(
        MetadataReader module, IReadOnlySet<string> closure, ModulePlatformSurface surface,
        ModuleLinkOptions options, MemberTally tally)
    {
        var provider = new CanonicalSignatureProvider();
        var pluginName = module.IsAssembly
            ? module.GetString(module.GetAssemblyDefinition().Name)
            : string.Empty;

        bool InScope(string assemblyName) =>
            assemblyName.StartsWith(PlatformAssemblyPrefix, StringComparison.Ordinal)
            && options.Judges(assemblyName)
            && surface.Carries(assemblyName)
            && (!closure.Contains(assemblyName) || surface.IsPlatformBound(assemblyName));

        // ── Every platform TYPE the module names must still be ACCESSIBLE to it. A type made
        // internal still exists by name — the type half reads it as present — and throws
        // TypeAccessException / MethodAccessException at the first use.
        foreach (var handle in module.TypeReferences)
        {
            var reference = module.GetTypeReference(handle);
            if (ResolveScope(module, reference) is not { } assemblyName || !InScope(assemblyName))
                continue;
            if (surface.IsDeclaredOnly(assemblyName))
                return false;
            var typeName = FullName(module, reference);
            if (Locate(surface, assemblyName, typeName, depth: 0) is not { } located)
                continue; // absent: the type half names it
            if (!IsVisible(located.Item1, located.Item2, pluginName))
                tally.Missing.Add($"{typeName} ({assemblyName}) — no longer public: TypeAccessException at first use");
        }

        // ── What the module's OWN types owe the platform types they derive from or implement: a
        // new abstract/interface member the plugin type does not implement, or a base class that
        // became sealed, is a TypeLoadException the moment the plugin type loads.
        MeasureObligations(module, surface, InScope, tally);

        foreach (var handle in module.MemberReferences)
        {
            var reference = module.GetMemberReference(handle);
            if (ParentTypeReference(module, reference.Parent) is not { } parentHandle)
                continue; // a method-local vararg site, a module-level ref, or an array type's method
            var parent = module.GetTypeReference(parentHandle);
            if (ResolveScope(module, parent) is not { } assemblyName || !InScope(assemblyName))
                continue;
            if (surface.IsDeclaredOnly(assemblyName))
                return false;

            var typeName = FullName(module, parent);
            var memberName = module.GetString(reference.Name);
            string signature;
            try
            {
                signature = reference.GetKind() == MemberReferenceKind.Field
                    ? reference.DecodeFieldSignature(provider, null)
                    : MethodSignature(reference.DecodeMethodSignature(provider, null));
            }
            catch (BadImageFormatException)
            {
                tally.Unverified.Add($"{typeName}::{memberName} ({assemblyName}) — unreadable signature");
                continue;
            }

            var described = $"{typeName}::{memberName}{DescribeSignature(signature, reference.GetKind())} ({assemblyName})";
            switch (FindMember(surface, assemblyName, typeName, memberName, signature,
                        reference.GetKind() == MemberReferenceKind.Field, provider, pluginName))
            {
                case MemberLookup.Found:
                    tally.Checked++;
                    break;
                case MemberLookup.Missing:
                    tally.Checked++;
                    tally.Missing.Add(described);
                    break;
                case MemberLookup.Inaccessible:
                    tally.Checked++;
                    tally.Missing.Add(described + " — no longer accessible: MethodAccessException / FieldAccessException at first use");
                    break;
                case MemberLookup.TypeAbsent:
                    // The type half has already named the type; counting the member again would
                    // report one break twice.
                    break;
                default:
                    tally.Unverified.Add(described);
                    break;
            }
        }
        return true;
    }

    private enum MemberLookup { Found, Missing, Inaccessible, TypeAbsent, Unverifiable }

    /// <summary>The TypeReference a member reference's parent names — directly, or as the generic
    /// type definition of a <c>GENERICINST</c> type specification (a member of <c>Foo&lt;int&gt;</c>).
    /// Null for anything else.</summary>
    private static TypeReferenceHandle? ParentTypeReference(MetadataReader metadata, EntityHandle parent)
    {
        if (parent.Kind == HandleKind.TypeReference)
            return (TypeReferenceHandle)parent;
        if (parent.Kind != HandleKind.TypeSpecification)
            return null;
        var blob = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
            return null;
        _ = blob.ReadCompressedInteger(); // CLASS or VALUETYPE
        var generic = blob.ReadTypeHandle();
        return generic.Kind == HandleKind.TypeReference ? (TypeReferenceHandle)generic : null;
    }

    /// <summary>
    /// Finds <paramref name="memberName"/> with <paramref name="signature"/> on the platform type,
    /// following type forwarders to the assembly that DEFINES it and then the base-type chain and
    /// implemented interfaces — the places the runtime's own member resolution looks.
    /// </summary>
    private static MemberLookup FindMember(
        ModulePlatformSurface surface, string assemblyName, string typeName, string memberName,
        string signature, bool isField, CanonicalSignatureProvider provider, string pluginName)
    {
        if (Locate(surface, assemblyName, typeName, depth: 0) is not { } start)
            return surface.MetadataOf(assemblyName) is null ? MemberLookup.Unverifiable : MemberLookup.TypeAbsent;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(MetadataReader Reader, TypeDefinitionHandle Type)>();
        pending.Push(start);
        var leftTheSurface = false;
        while (pending.Count > 0)
        {
            var (reader, handle) = pending.Pop();
            var definition = reader.GetTypeDefinition(handle);
            if (!visited.Add(FullNameOf(reader, handle)))
                continue;

            if (isField)
            {
                foreach (var fieldHandle in definition.GetFields())
                {
                    var field = reader.GetFieldDefinition(fieldHandle);
                    if (reader.StringComparer.Equals(field.Name, memberName)
                        && field.DecodeSignature(provider, null) == signature)
                        return IsAccessible((int)(field.Attributes & System.Reflection.FieldAttributes.FieldAccessMask), reader, pluginName)
                            ? MemberLookup.Found
                            : MemberLookup.Inaccessible;
                }
            }
            else
            {
                foreach (var methodHandle in definition.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    if (reader.StringComparer.Equals(method.Name, memberName)
                        && MethodSignature(method.DecodeSignature(provider, null)) == signature)
                        return IsAccessible((int)(method.Attributes & System.Reflection.MethodAttributes.MemberAccessMask), reader, pluginName)
                            ? MemberLookup.Found
                            : MemberLookup.Inaccessible;
                }
            }

            // Constructors are never inherited; everything else may be declared further up.
            if (memberName is ".ctor" or ".cctor")
                continue;
            foreach (var next in Supertypes(surface, reader, definition))
            {
                if (next is { } found)
                    pending.Push(found);
                else
                    leftTheSurface = true;
            }
        }
        return leftTheSurface ? MemberLookup.Unverifiable : MemberLookup.Missing;
    }

    /// <summary>The base type and the implemented interfaces of a definition, resolved into the
    /// surface; a null entry marks a supertype the surface cannot read (the chain LEFT it).
    /// <c>System.Object</c> and <c>System.ValueType</c> are roots, not exits.</summary>
    private static IEnumerable<(MetadataReader, TypeDefinitionHandle)?> Supertypes(
        ModulePlatformSurface surface, MetadataReader reader, TypeDefinition definition)
    {
        var supertypes = new List<EntityHandle>();
        if (!definition.BaseType.IsNil)
            supertypes.Add(definition.BaseType);
        foreach (var implementation in definition.GetInterfaceImplementations())
            supertypes.Add(reader.GetInterfaceImplementation(implementation).Interface);

        foreach (var supertype in supertypes)
        {
            switch (supertype.Kind)
            {
                case HandleKind.TypeDefinition:
                    yield return (reader, (TypeDefinitionHandle)supertype);
                    break;
                case HandleKind.TypeReference:
                case HandleKind.TypeSpecification:
                    if (ParentTypeReference(reader, supertype) is not { } referenceHandle)
                    {
                        yield return null;
                        break;
                    }
                    var reference = reader.GetTypeReference(referenceHandle);
                    var name = FullName(reader, reference);
                    if (name is "System.Object" or "System.ValueType" or "System.Enum")
                        break;
                    yield return ResolveScope(reader, reference) is { } scope
                        ? Locate(surface, scope, name, depth: 0)
                        : null;
                    break;
            }
        }
    }

    /// <summary>The definition of <paramref name="typeName"/> in the platform copy of
    /// <paramref name="assemblyName"/>, following exported-type forwarders (bounded).</summary>
    private static (MetadataReader, TypeDefinitionHandle)? Locate(
        ModulePlatformSurface surface, string assemblyName, string typeName, int depth)
    {
        if (depth > 8 || surface.MetadataOf(assemblyName) is not { } reader)
            return null;
        foreach (var handle in reader.TypeDefinitions)
            if (FullNameOf(reader, handle) == typeName)
                return (reader, handle);
        foreach (var handle in reader.ExportedTypes)
        {
            var exported = reader.GetExportedType(handle);
            if (ExportedFullName(reader, exported) != typeName)
                continue;
            var implementation = exported.Implementation;
            while (implementation.Kind == HandleKind.ExportedType)
                implementation = reader.GetExportedType((ExportedTypeHandle)implementation).Implementation;
            if (implementation.Kind != HandleKind.AssemblyReference)
                return null;
            var target = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)implementation).Name);
            return Locate(surface, target, typeName, depth + 1);
        }
        return null;
    }

    /// <summary>
    /// Whether a member access level (the shared encoding of <c>MemberAccessMask</c> and
    /// <c>FieldAccessMask</c>) is reachable from another assembly: public, protected, or
    /// protected-internal — or internal / private-protected, when the platform assembly grants
    /// <c>InternalsVisibleTo</c> to the plugin.
    /// </summary>
    private static bool IsAccessible(int access, MetadataReader platform, string pluginName) => access switch
    {
        6 or 4 or 5 => true,                                // Public, Family, FamORAssem
        3 or 2 => GrantsInternalsTo(platform, pluginName),  // Assembly, FamANDAssem
        _ => false,                                         // Private, PrivateScope
    };

    /// <summary>Whether a platform type is visible to the plugin: public (nested: public or
    /// protected inside a visible declaring type), or internal to an assembly that grants the plugin
    /// <c>InternalsVisibleTo</c>.</summary>
    private static bool IsVisible(MetadataReader reader, TypeDefinitionHandle handle, string pluginName)
    {
        var type = reader.GetTypeDefinition(handle);
        var visibility = type.Attributes & System.Reflection.TypeAttributes.VisibilityMask;
        var declaring = type.GetDeclaringType();
        return visibility switch
        {
            System.Reflection.TypeAttributes.Public => true,
            System.Reflection.TypeAttributes.NestedPublic
                or System.Reflection.TypeAttributes.NestedFamily
                or System.Reflection.TypeAttributes.NestedFamORAssem =>
                declaring.IsNil || IsVisible(reader, declaring, pluginName),
            System.Reflection.TypeAttributes.NotPublic => GrantsInternalsTo(reader, pluginName),
            System.Reflection.TypeAttributes.NestedAssembly
                or System.Reflection.TypeAttributes.NestedFamANDAssem =>
                GrantsInternalsTo(reader, pluginName)
                && (declaring.IsNil || IsVisible(reader, declaring, pluginName)),
            _ => false,
        };
    }

    /// <summary>Whether <paramref name="platform"/> carries an <c>InternalsVisibleTo</c> naming
    /// <paramref name="pluginName"/>.</summary>
    private static bool GrantsInternalsTo(MetadataReader platform, string pluginName)
    {
        if (string.IsNullOrEmpty(pluginName) || !platform.IsAssembly)
            return false;
        foreach (var handle in platform.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = platform.GetCustomAttribute(handle);
            if (AttributeTypeName(platform, attribute) != "System.Runtime.CompilerServices.InternalsVisibleToAttribute")
                continue;
            var value = platform.GetBlobReader(attribute.Value);
            if (value.Length < 2 || value.ReadUInt16() != 1) // the custom-attribute blob prolog
                continue;
            var simple = value.ReadSerializedString()?.Split(',')[0].Trim();
            if (string.Equals(simple, pluginName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? AttributeTypeName(MetadataReader reader, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var parent = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                return parent.Kind == HandleKind.TypeReference
                    ? FullName(reader, reader.GetTypeReference((TypeReferenceHandle)parent))
                    : null;
            case HandleKind.MethodDefinition:
                return FullNameOf(reader,
                    reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType());
            default:
                return null;
        }
    }

    /// <summary>
    /// 🚨 The IMPLEMENTER half (the #3465 shape, at run time). For every type the MODULE defines,
    /// the platform supertypes it derives from or implements are resolved on the new platform, and
    /// <list type="bullet">
    /// <item><description>a platform base class that is now <c>sealed</c> is a break;</description></item>
    /// <item><description>an ABSTRACT member of a platform base class, or an interface member with
    /// no default implementation, that the module type does not implement is a break — the loader
    /// refuses the type with <c>TypeLoadException</c> ("Method … does not have an implementation")
    /// the moment it loads, whatever code path first touches it.</description></item>
    /// </list>
    /// Implementation is matched by NAME and PARAMETER COUNT (implicit) or by an explicit
    /// <c>MethodImpl</c> naming the member — generic substitution between an interface's signature
    /// and the implementer's is deliberately not attempted, so this can MISS an implementation gap
    /// that differs only in a parameter type; it never reports an implementation that exists.
    /// </summary>
    private static void MeasureObligations(
        MetadataReader module, ModulePlatformSurface surface, Func<string, bool> inScope,
        MemberTally tally)
    {
        foreach (var typeHandle in module.TypeDefinitions)
        {
            var type = module.GetTypeDefinition(typeHandle);
            if ((type.Attributes & System.Reflection.TypeAttributes.Interface) != 0)
                continue;
            var typeName = FullNameOf(module, typeHandle);
            if (typeName == "<Module>")
                continue;
            var isAbstract = (type.Attributes & System.Reflection.TypeAttributes.Abstract) != 0;

            // What this type (and its module-local bases) implements: implicit (name/arity) and
            // explicit MethodImpl declarations by member name.
            var implemented = new HashSet<string>(StringComparer.Ordinal);
            var explicitly = new HashSet<string>(StringComparer.Ordinal);
            var platformSupertypes = new List<(MetadataReader Reader, TypeDefinitionHandle Handle, string Assembly, bool IsDirectBase)>();

            bool AddPlatformSupertype(EntityHandle supertype, bool isDirectBase)
            {
                if (ParentTypeReference(module, supertype) is not { } referenceHandle)
                    return false;
                var reference = module.GetTypeReference(referenceHandle);
                if (ResolveScope(module, reference) is not { } assemblyName || !inScope(assemblyName))
                    return false;
                if (Locate(surface, assemblyName, FullName(module, reference), depth: 0) is not { } located)
                    return false;
                platformSupertypes.Add((located.Item1, located.Item2, assemblyName, isDirectBase));
                return true;
            }

            var cursor = typeHandle;
            var isOwnType = true;
            // True when the module-local chain ends at a base OUTSIDE the judged platform and
            // outside System.Object (a sibling module's class): what that base implements cannot be
            // read here, so the obligation half stays silent rather than guess — a miss, never a
            // false alarm.
            var inheritsUnreadable = false;
            while (true)
            {
                var current = module.GetTypeDefinition(cursor);
                foreach (var methodHandle in current.GetMethods())
                {
                    var method = module.GetMethodDefinition(methodHandle);
                    if ((method.Attributes & System.Reflection.MethodAttributes.Abstract) == 0)
                        implemented.Add(module.GetString(method.Name) + "/" + Arity(module, method));
                }
                foreach (var implHandle in current.GetMethodImplementations())
                {
                    var declaration = module.GetMethodImplementation(implHandle).MethodDeclaration;
                    if (declaration.Kind == HandleKind.MemberReference)
                        explicitly.Add(module.GetString(module.GetMemberReference((MemberReferenceHandle)declaration).Name));
                }
                foreach (var implementation in current.GetInterfaceImplementations())
                    AddPlatformSupertype(module.GetInterfaceImplementation(implementation).Interface, isDirectBase: false);
                var baseType = current.BaseType;
                if (baseType.Kind == HandleKind.TypeDefinition)
                {
                    cursor = (TypeDefinitionHandle)baseType;
                    isOwnType = false;
                    continue;
                }
                if (!baseType.IsNil && !AddPlatformSupertype(baseType, isDirectBase: isOwnType)
                    && !IsRootType(module, baseType))
                    inheritsUnreadable = true;
                break;
            }

            foreach (var (reader, handle, assembly, isDirectBase) in platformSupertypes)
            {
                var supertype = reader.GetTypeDefinition(handle);
                var supertypeName = FullNameOf(reader, handle);
                if (isDirectBase && (supertype.Attributes & System.Reflection.TypeAttributes.Sealed) != 0)
                {
                    tally.Missing.Add($"{typeName} derives from {supertypeName} ({assembly}), which is now "
                                      + $"sealed: TypeLoadException when {typeName} loads");
                    continue;
                }
                if (isAbstract || inheritsUnreadable)
                    continue; // an abstract module type may leave members to its own subclasses
                foreach (var (owner, ownerReader, method) in AbstractMembers(surface, reader, handle))
                {
                    var name = ownerReader.GetString(method.Name);
                    var arity = Arity(ownerReader, method);
                    if (implemented.Contains(name + "/" + arity) || explicitly.Contains(name)
                        || ImplementedByPlatformBase(surface, platformSupertypes, name, arity))
                        continue;
                    tally.Missing.Add(
                        $"{typeName} does not implement {owner}::{name} ({assembly}) — a member the "
                        + $"platform added without a default implementation: TypeLoadException when {typeName} loads");
                }
            }
        }
    }

    /// <summary>Whether a base type is one of the runtime's roots every class ends at.</summary>
    private static bool IsRootType(MetadataReader module, EntityHandle baseType) =>
        ParentTypeReference(module, baseType) is { } handle
        && FullName(module, module.GetTypeReference(handle)) is "System.Object" or "System.ValueType"
            or "System.Enum" or "System.MulticastDelegate" or "System.Attribute" or "System.Exception";

    /// <summary>Whether a platform reader is one of the platform's OWN assemblies — the obligation
    /// walk stays inside them; the base class library's contracts are the runtime's.</summary>
    private static bool IsPlatformAssembly(MetadataReader reader) =>
        reader.IsAssembly
        && reader.GetString(reader.GetAssemblyDefinition().Name)
            .StartsWith(PlatformAssemblyPrefix, StringComparison.Ordinal);

    private static int Arity(MetadataReader reader, MethodDefinition method) =>
        method.DecodeSignature(new ArityProvider(), null).ParameterTypes.Length;

    /// <summary>The abstract members a non-abstract implementer of <paramref name="handle"/> owes:
    /// abstract instance methods along a class's platform base chain that nothing further down the
    /// chain implements, and body-less instance members of an interface and of every interface it
    /// extends.</summary>
    private static List<(string Owner, MetadataReader Reader, MethodDefinition Method)> AbstractMembers(
        ModulePlatformSurface surface, MetadataReader reader, TypeDefinitionHandle handle)
    {
        var owed = new List<(string, MetadataReader, MethodDefinition)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var concrete = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<(MetadataReader, TypeDefinitionHandle)>();
        pending.Enqueue((reader, handle));
        while (pending.Count > 0)
        {
            var (currentReader, current) = pending.Dequeue();
            var name = FullNameOf(currentReader, current);
            if (!visited.Add(name) || !IsPlatformAssembly(currentReader))
                continue;
            var definition = currentReader.GetTypeDefinition(current);
            foreach (var methodHandle in definition.GetMethods())
            {
                var method = currentReader.GetMethodDefinition(methodHandle);
                var key = currentReader.GetString(method.Name) + "/" + Arity(currentReader, method);
                if ((method.Attributes & System.Reflection.MethodAttributes.Abstract) == 0)
                {
                    concrete.Add(key);
                    continue;
                }
                if ((method.Attributes & System.Reflection.MethodAttributes.Static) == 0 && !concrete.Contains(key))
                    owed.Add((name, currentReader, method));
            }
            foreach (var next in Supertypes(surface, currentReader, definition))
                if (next is { } found)
                    pending.Enqueue(found);
        }
        return owed;
    }

    /// <summary>Whether a platform base CLASS among the supertypes already provides a concrete
    /// member of that name and arity — a platform base implementing an interface member for its
    /// subclasses.</summary>
    private static bool ImplementedByPlatformBase(
        ModulePlatformSurface surface,
        List<(MetadataReader Reader, TypeDefinitionHandle Handle, string Assembly, bool IsDirectBase)> supertypes,
        string name, int arity)
    {
        foreach (var (reader, handle, _, _) in supertypes)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<(MetadataReader, TypeDefinitionHandle)>();
            pending.Push((reader, handle));
            while (pending.Count > 0)
            {
                var (currentReader, current) = pending.Pop();
                if (!visited.Add(FullNameOf(currentReader, current)))
                    continue;
                var definition = currentReader.GetTypeDefinition(current);
                if ((definition.Attributes & System.Reflection.TypeAttributes.Interface) != 0)
                    continue;
                foreach (var methodHandle in definition.GetMethods())
                {
                    var method = currentReader.GetMethodDefinition(methodHandle);
                    if ((method.Attributes & System.Reflection.MethodAttributes.Abstract) == 0
                        && currentReader.StringComparer.Equals(method.Name, name)
                        && Arity(currentReader, method) == arity)
                        return true;
                }
                // An EXPLICIT implementation (`void IFoo.Bar()`) is named `IFoo.Bar` in metadata;
                // its MethodImpl row is what declares which member it implements.
                foreach (var implHandle in definition.GetMethodImplementations())
                {
                    var declaration = currentReader.GetMethodImplementation(implHandle).MethodDeclaration;
                    var declared = declaration.Kind switch
                    {
                        HandleKind.MemberReference => currentReader.GetString(
                            currentReader.GetMemberReference((MemberReferenceHandle)declaration).Name),
                        HandleKind.MethodDefinition => currentReader.GetString(
                            currentReader.GetMethodDefinition((MethodDefinitionHandle)declaration).Name),
                        _ => null,
                    };
                    if (declared == name)
                        return true;
                }
                if (definition.BaseType.Kind == HandleKind.TypeDefinition)
                    pending.Push((currentReader, (TypeDefinitionHandle)definition.BaseType));
                else if (!definition.BaseType.IsNil
                         && ParentTypeReference(currentReader, definition.BaseType) is { } baseReference)
                {
                    var reference = currentReader.GetTypeReference(baseReference);
                    if (ResolveScope(currentReader, reference) is { } scope
                        && Locate(surface, scope, FullName(currentReader, reference), depth: 0) is { } located)
                        pending.Push(located);
                }
            }
        }
        return false;
    }

    /// <summary>Decodes a signature for its SHAPE only (parameter count) — no type names.</summary>
    private sealed class ArityProvider : ISignatureTypeProvider<int, object?>
    {
        public int GetPrimitiveType(PrimitiveTypeCode typeCode) => 0;
        public int GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => 0;
        public int GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => 0;
        public int GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => 0;
        public int GetSZArrayType(int elementType) => 0;
        public int GetArrayType(int elementType, ArrayShape shape) => 0;
        public int GetByReferenceType(int elementType) => 0;
        public int GetPointerType(int elementType) => 0;
        public int GetPinnedType(int elementType) => 0;
        public int GetGenericInstantiation(int genericType, ImmutableArray<int> typeArguments) => 0;
        public int GetGenericTypeParameter(object? genericContext, int index) => 0;
        public int GetGenericMethodParameter(object? genericContext, int index) => 0;
        public int GetModifiedType(int modifier, int unmodifiedType, bool isRequired) => 0;
        public int GetFunctionPointerType(MethodSignature<int> signature) => 0;
    }

    private static string FullNameOf(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
            return FullNameOf(reader, declaring) + "+" + name;
        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string ExportedFullName(MetadataReader reader, ExportedType type)
    {
        var name = reader.GetString(type.Name);
        if (type.Implementation.Kind == HandleKind.ExportedType)
            return ExportedFullName(reader, reader.GetExportedType((ExportedTypeHandle)type.Implementation)) + "+" + name;
        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    /// <summary>A method signature in one canonical spelling — calling convention, generic arity,
    /// return type and parameter types, each type by full name with its assembly dropped (the
    /// runtime matches a MemberRef against a MethodDef exactly this way: by the SHAPE of the
    /// signature, with type identity by name once forwarding is followed).</summary>
    private static string MethodSignature(MethodSignature<string> signature) =>
        $"{(signature.Header.IsInstance ? "instance " : "")}{signature.Header.CallingConvention}"
        + $"`{signature.GenericParameterCount} {signature.ReturnType} "
        + $"({string.Join(",", signature.ParameterTypes)})";

    private static string DescribeSignature(string signature, MemberReferenceKind kind) =>
        kind == MemberReferenceKind.Field ? $" : {signature}" : $" [{signature}]";

    /// <summary>Decodes signature types into canonical strings: full names without assembly,
    /// generic parameters as <c>!n</c> / <c>!!n</c>, modifiers kept (a <c>modreq</c> — an init-only
    /// setter, an <c>in</c> parameter — is part of the runtime's match).</summary>
    private sealed class CanonicalSignatureProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Void => "System.Void",
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.SByte => "System.SByte",
            PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.Int16 => "System.Int16",
            PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.UInt64 => "System.UInt64",
            PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.IntPtr => "System.IntPtr",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
            PrimitiveTypeCode.TypedReference => "System.TypedReference",
            _ => typeCode.ToString(),
        };

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            FullNameOf(reader, handle);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            FullName(reader, reader.GetTypeReference(handle));

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext,
            TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape) =>
            elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPinnedType(string elementType) => elementType + " pinned";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            genericType + "<" + string.Join(",", typeArguments) + ">";

        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
            unmodifiedType + (isRequired ? " modreq(" : " modopt(") + modifier + ")";

        public string GetFunctionPointerType(MethodSignature<string> signature)
        {
            var builder = new StringBuilder("method ");
            builder.Append(MethodSignature(signature));
            return builder.ToString();
        }
    }
}
