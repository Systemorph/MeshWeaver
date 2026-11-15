using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace MeshWeaver.PluginTester;

/// <summary>
/// A DETERMINISTIC hash of what a COMPILER can see of an assembly from outside it — the part of a
/// reference that decides the bytes of a NodeType compiled against it. Read from metadata and
/// written out as NAMES, never as row numbers or tokens, so two builds of unchanged source hash
/// equal even when their MVIDs, their provenance stamps and their private members differ
/// (<c>Doc/Architecture/NodeTypeCompileCache</c>).
///
/// <para><b>Why not the reference assembly's bytes.</b> Roslyn's ref-assembly emit is byte-stable
/// only inside ONE build invocation: measured on one commit, 4 of 37 canonical ref assemblies
/// differed between the Build-and-Test output and the image publish
/// (<c>MeshWeaverSurfaceManifest.targets</c>), and every CI-built <c>MeshWeaver.*</c> carries a
/// <c>g&lt;commit&gt;</c> <see cref="AssemblyMetadataAttribute"/> that moves on every commit. A
/// key over those bytes would miss on every run — exactly the 0 % hit rate this exists to end.</para>
///
/// <para><b>What counts</b>, every rule erring toward a DIFFERENT hash (a rebuild, never a wrong
/// reuse): the assembly's name, version, culture and public key; its assembly- and module-level
/// attributes except the provenance ones in <see cref="ProvenanceAttributes"/>; its type
/// forwarders; and for every type visible outside the assembly — public, or nested
/// public/protected/protected-internal inside such a type — its flags, base type, interfaces,
/// generic parameters and constraints, attributes, layout, and every public, protected and
/// protected-internal member with its signature, parameter names, defaults, constants and
/// attributes. Beyond the visible surface it also counts every ABSTRACT member (an internal one
/// still decides whether outside code may derive), every instance field of a VALUE type (they
/// decide definite assignment and managed-ness), and every explicit interface implementation.</para>
///
/// <para><b>One escape hatch, by name.</b> An assembly granting <c>InternalsVisibleTo</c> to a
/// NodeType's own compile (<c>DynamicNode_*</c>) exposes its internals to that compile, so its
/// "surface" is everything: it is keyed by its MVID instead.</para>
/// </summary>
internal static class AssemblySurface
{
    /// <summary>The scheme of a surface hash; bumped on any change to what is hashed.</summary>
    public const string Scheme = "s1:";

    /// <summary>The scheme of an assembly keyed by its exact build (see the IVT rule above).</summary>
    public const string MvidScheme = "mvid:";

    /// <summary>The prefix every NodeType compile's assembly name starts with
    /// (<c>NodeSetCompiler.ResolveInputs</c>: <c>DynamicNode_&lt;sanitized path&gt;</c>).</summary>
    public const string NodeTypeAssemblyPrefix = "DynamicNode_";

    /// <summary>
    /// Assembly-level attributes that record WHERE and WHEN a build happened and are invisible to
    /// a consumer's compile. Every other attribute is hashed.
    /// </summary>
    internal static readonly ImmutableHashSet<string> ProvenanceAttributes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "System.Reflection.AssemblyMetadataAttribute",
        "System.Reflection.AssemblyInformationalVersionAttribute",
        "System.Reflection.AssemblyFileVersionAttribute",
        "System.Reflection.AssemblyConfigurationAttribute",
        "System.Reflection.AssemblyCompanyAttribute",
        "System.Reflection.AssemblyProductAttribute",
        "System.Reflection.AssemblyTitleAttribute",
        "System.Reflection.AssemblyCopyrightAttribute",
        "System.Reflection.AssemblyTrademarkAttribute",
        "System.Reflection.AssemblyDescriptionAttribute",
        "System.Diagnostics.DebuggableAttribute");

    /// <summary>The surface id of the assembly at <paramref name="path"/>: <c>s1:&lt;sha256&gt;</c>,
    /// or <c>mvid:&lt;id&gt;</c> under the IVT rule.</summary>
    public static string Of(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        return Of(reader);
    }

    /// <summary>The surface id of the assembly <paramref name="reader"/> reads.</summary>
    internal static string Of(MetadataReader reader)
    {
        if (GrantsInternalsToNodeTypes(reader))
            return MvidScheme + reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString("N");
        return Scheme + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Describe(reader))));
    }

    /// <summary>The text that is hashed — one line per fact, sorted. Exposed for diagnostics and
    /// for the tests that pin what does and does not move it.</summary>
    internal static string Describe(MetadataReader reader)
    {
        var names = new Names(reader);
        var lines = new List<string>();
        if (reader.IsAssembly)
        {
            var assembly = reader.GetAssemblyDefinition();
            lines.Add(Invariant(
                $"asm {reader.GetString(assembly.Name)} {assembly.Version} {reader.GetString(assembly.Culture)} {Convert.ToHexStringLower(reader.GetBlobBytes(assembly.PublicKey))}"));
            foreach (var attribute in names.Attributes(assembly.GetCustomAttributes(), skipProvenance: true))
                lines.Add("asm-attr " + attribute);
        }
        foreach (var attribute in names.Attributes(reader.GetModuleDefinition().GetCustomAttributes(), skipProvenance: false))
            lines.Add("module-attr " + attribute);
        foreach (var handle in reader.ExportedTypes)
        {
            var exported = reader.GetExportedType(handle);
            lines.Add($"forward {reader.GetString(exported.Namespace)}.{reader.GetString(exported.Name)} "
                + $"{names.ImplementationOf(exported.Implementation)} {(int)exported.Attributes}");
        }
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (!IsVisible(reader, type))
                continue;
            var typeName = names.DefName(handle);
            foreach (var line in DescribeType(reader, names, type))
                lines.Add(typeName + " " + line);
        }
        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    private static IEnumerable<string> DescribeType(MetadataReader reader, Names names, TypeDefinition type)
    {
        var isValueType = names.IsValueType(type);
        yield return Invariant($"flags {(int)(type.Attributes & ~TypeAttributes.BeforeFieldInit)}");
        if (!type.BaseType.IsNil)
            yield return "base " + names.TypeOf(type.BaseType);
        foreach (var implHandle in type.GetInterfaceImplementations())
        {
            var impl = reader.GetInterfaceImplementation(implHandle);
            yield return "implements " + names.TypeOf(impl.Interface)
                + string.Concat(names.Attributes(impl.GetCustomAttributes(), false).Select(a => " @" + a));
        }
        foreach (var line in names.GenericParameters(type.GetGenericParameters(), "type-generic"))
            yield return line;
        foreach (var attribute in names.Attributes(type.GetCustomAttributes(), false))
            yield return "attr " + attribute;
        if (isValueType || (type.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.AutoLayout)
        {
            var layout = type.GetLayout();
            yield return Invariant($"layout {layout.PackingSize} {layout.Size}");
        }

        var generic = new GenericContext(type.GetGenericParameters(), default);
        foreach (var fieldHandle in type.GetFields())
        {
            var field = reader.GetFieldDefinition(fieldHandle);
            var visible = IsVisibleMember((int)(field.Attributes & FieldAttributes.FieldAccessMask));
            var isInstance = (field.Attributes & FieldAttributes.Static) == 0;
            if (!visible && !(isValueType && isInstance))
                continue;
            var signature = field.DecodeSignature(names, generic);
            // A hidden instance field of a struct counts by TYPE only — its name is private.
            var name = visible ? reader.GetString(field.Name) : "<hidden>";
            var line = Invariant($"field {name} {(int)field.Attributes} {signature} offset={field.GetOffset()}");
            if (visible)
            {
                line += names.ConstantOf(field.GetDefaultValue());
                line += string.Concat(names.Attributes(field.GetCustomAttributes(), false).Select(a => " @" + a));
            }
            yield return line;
        }

        var visibleMethods = new HashSet<MethodDefinitionHandle>();
        foreach (var methodHandle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(methodHandle);
            var visible = IsVisibleMember((int)(method.Attributes & MethodAttributes.MemberAccessMask));
            var isAbstract = (method.Attributes & MethodAttributes.Abstract) != 0;
            if (!visible && !isAbstract)
                continue;
            if (visible)
                visibleMethods.Add(methodHandle);
            yield return "method " + names.MethodLine(methodHandle, method, generic);
        }

        // Explicit interface implementations are private methods, and they still decide what a
        // consumer can bind through the interface.
        foreach (var implHandle in type.GetMethodImplementations())
        {
            var impl = reader.GetMethodImplementation(implHandle);
            yield return $"methodimpl {names.MethodRef(impl.MethodBody, generic)} => {names.MethodRef(impl.MethodDeclaration, generic)}";
        }

        foreach (var propertyHandle in type.GetProperties())
        {
            var property = reader.GetPropertyDefinition(propertyHandle);
            var accessors = property.GetAccessors();
            if (!AnyVisible(reader, accessors.Getter, accessors.Setter, accessors.Others))
                continue;
            yield return Invariant(
                    $"property {reader.GetString(property.Name)} {(int)property.Attributes} {names.PropertySignature(property.DecodeSignature(names, generic))} get={names.AccessorName(accessors.Getter)} set={names.AccessorName(accessors.Setter)}")
                + names.ConstantOf(property.GetDefaultValue())
                + string.Concat(names.Attributes(property.GetCustomAttributes(), false).Select(a => " @" + a));
        }
        foreach (var eventHandle in type.GetEvents())
        {
            var @event = reader.GetEventDefinition(eventHandle);
            var accessors = @event.GetAccessors();
            if (!AnyVisible(reader, accessors.Adder, accessors.Remover, accessors.Raiser, accessors.Others))
                continue;
            yield return Invariant(
                    $"event {reader.GetString(@event.Name)} {(int)@event.Attributes} {names.TypeOf(@event.Type)} add={names.AccessorName(accessors.Adder)} remove={names.AccessorName(accessors.Remover)}")
                + string.Concat(names.Attributes(@event.GetCustomAttributes(), false).Select(a => " @" + a));
        }
    }

    private static bool AnyVisible(MetadataReader reader, params object[] accessors)
    {
        foreach (var accessor in accessors)
        {
            switch (accessor)
            {
                case MethodDefinitionHandle handle when !handle.IsNil:
                    if (IsVisibleMember((int)(reader.GetMethodDefinition(handle).Attributes & MethodAttributes.MemberAccessMask)))
                        return true;
                    break;
                case ImmutableArray<MethodDefinitionHandle> others:
                    foreach (var other in others)
                        if (IsVisibleMember((int)(reader.GetMethodDefinition(other).Attributes & MethodAttributes.MemberAccessMask)))
                            return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>Public (6), Family (4) and FamORAssem (5) — the same values for methods and fields.</summary>
    private static bool IsVisibleMember(int access) => access is 4 or 5 or 6;

    /// <summary>Visible outside the assembly: public, or nested public/family/famORassem in a
    /// visible type.</summary>
    internal static bool IsVisible(MetadataReader reader, TypeDefinition type)
    {
        switch (type.Attributes & TypeAttributes.VisibilityMask)
        {
            case TypeAttributes.Public:
                return true;
            case TypeAttributes.NestedPublic:
            case TypeAttributes.NestedFamily:
            case TypeAttributes.NestedFamORAssem:
                return IsVisible(reader, reader.GetTypeDefinition(type.GetDeclaringType()));
            default:
                return false;
        }
    }

    /// <summary>True when an <c>InternalsVisibleTo</c> names a NodeType compile.</summary>
    internal static bool GrantsInternalsToNodeTypes(MetadataReader reader)
    {
        if (!reader.IsAssembly)
            return false;
        var names = new Names(reader);
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (names.AttributeTypeName(attribute.Constructor) != "System.Runtime.CompilerServices.InternalsVisibleToAttribute")
                continue;
            try
            {
                var blob = reader.GetBlobReader(attribute.Value);
                if (blob.ReadUInt16() != 1)
                    return true; // unreadable ⇒ assume the worst
                var friend = blob.ReadSerializedString() ?? string.Empty;
                if (friend.StartsWith(NodeTypeAssemblyPrefix, StringComparison.Ordinal))
                    return true;
            }
            catch (BadImageFormatException)
            {
                return true;
            }
        }
        return false;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>The generic context a signature is decoded in — only the PARAMETER INDEX is ever
    /// printed (<c>!0</c>, <c>!!0</c>), so the context carries nothing the provider reads.</summary>
    private readonly record struct GenericContext(GenericParameterHandleCollection Type, GenericParameterHandleCollection Method);

    /// <summary>Decodes handles and signatures into names — never a token, never a row number.</summary>
    private sealed class Names(MetadataReader reader) : ISignatureTypeProvider<string, GenericContext>
    {
        public string DefName(TypeDefinitionHandle handle)
        {
            var type = reader.GetTypeDefinition(handle);
            var name = reader.GetString(type.Name);
            var declaring = type.GetDeclaringType();
            if (!declaring.IsNil)
                return DefName(declaring) + "/" + name;
            var ns = reader.GetString(type.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string RefName(TypeReferenceHandle handle)
        {
            var type = reader.GetTypeReference(handle);
            var name = reader.GetString(type.Name);
            var ns = reader.GetString(type.Namespace);
            var local = ns.Length == 0 ? name : ns + "." + name;
            var scope = type.ResolutionScope;
            return scope.Kind switch
            {
                HandleKind.TypeReference => RefName((TypeReferenceHandle)scope) + "/" + name,
                HandleKind.AssemblyReference =>
                    "[" + reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) + "]" + local,
                HandleKind.ModuleReference =>
                    "[." + reader.GetString(reader.GetModuleReference((ModuleReferenceHandle)scope).Name) + "]" + local,
                _ => local,
            };
        }

        public bool IsValueType(TypeDefinition type)
        {
            if (type.BaseType.IsNil)
                return false;
            var baseName = TypeOf(type.BaseType);
            return baseName.EndsWith("]System.ValueType", StringComparison.Ordinal)
                   || baseName.EndsWith("]System.Enum", StringComparison.Ordinal)
                   || baseName is "System.ValueType" or "System.Enum";
        }

        public string TypeOf(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => DefName((TypeDefinitionHandle)handle),
            HandleKind.TypeReference => RefName((TypeReferenceHandle)handle),
            HandleKind.TypeSpecification =>
                reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, default),
            _ => "?" + handle.Kind,
        };

        public string ImplementationOf(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.AssemblyReference => "[" + reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)handle).Name) + "]",
            HandleKind.ExportedType => "nested:" + reader.GetString(reader.GetExportedType((ExportedTypeHandle)handle).Name),
            HandleKind.AssemblyFile => "file:" + reader.GetString(reader.GetAssemblyFile((AssemblyFileHandle)handle).Name),
            _ => "?" + handle.Kind,
        };

        public string AttributeTypeName(EntityHandle constructor) => constructor.Kind switch
        {
            HandleKind.MethodDefinition => DefName(reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()),
            HandleKind.MemberReference => TypeOf(reader.GetMemberReference((MemberReferenceHandle)constructor).Parent),
            _ => "?" + constructor.Kind,
        };

        public IEnumerable<string> Attributes(CustomAttributeHandleCollection handles, bool skipProvenance)
        {
            var list = new List<string>();
            foreach (var handle in handles)
            {
                var attribute = reader.GetCustomAttribute(handle);
                var typeName = AttributeTypeName(attribute.Constructor);
                if (skipProvenance && ProvenanceAttributes.Contains(StripScope(typeName)))
                    continue;
                list.Add($"{typeName}::{ConstructorSignature(attribute.Constructor)}="
                    + Convert.ToHexStringLower(reader.GetBlobBytes(attribute.Value)));
            }
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static string StripScope(string typeName)
        {
            var close = typeName.IndexOf(']');
            return typeName.StartsWith('[') && close > 0 ? typeName[(close + 1)..] : typeName;
        }

        private string ConstructorSignature(EntityHandle constructor) => constructor.Kind switch
        {
            HandleKind.MethodDefinition => Signature(reader.GetMethodDefinition((MethodDefinitionHandle)constructor).DecodeSignature(this, default)),
            HandleKind.MemberReference => Signature(reader.GetMemberReference((MemberReferenceHandle)constructor).DecodeMethodSignature(this, default)),
            _ => "?",
        };

        public IEnumerable<string> GenericParameters(GenericParameterHandleCollection handles, string label)
        {
            foreach (var handle in handles)
            {
                var parameter = reader.GetGenericParameter(handle);
                var constraints = parameter.GetConstraints()
                    .Select(c =>
                    {
                        var constraint = reader.GetGenericParameterConstraint(c);
                        return TypeOf(constraint.Type)
                            + string.Concat(Attributes(constraint.GetCustomAttributes(), false).Select(a => " @" + a));
                    })
                    .OrderBy(s => s, StringComparer.Ordinal);
                yield return Invariant(
                        $"{label} {parameter.Index} {(int)parameter.Attributes} [{string.Join(",", constraints)}]")
                    + string.Concat(Attributes(parameter.GetCustomAttributes(), false).Select(a => " @" + a));
            }
        }

        public string MethodLine(MethodDefinitionHandle handle, MethodDefinition method, GenericContext typeContext)
        {
            var context = typeContext with { Method = method.GetGenericParameters() };
            var builder = new StringBuilder();
            builder.Append(reader.GetString(method.Name)).Append(' ')
                .Append(((int)method.Attributes).ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(Signature(method.DecodeSignature(this, context)));
            foreach (var line in GenericParameters(method.GetGenericParameters(), "generic"))
                builder.Append(" {").Append(line).Append('}');
            foreach (var parameterHandle in method.GetParameters())
            {
                var parameter = reader.GetParameter(parameterHandle);
                builder.Append(Invariant($" (p{parameter.SequenceNumber} {reader.GetString(parameter.Name)} {(int)parameter.Attributes}"))
                    .Append(ConstantOf(parameter.GetDefaultValue()))
                    .Append(string.Concat(Attributes(parameter.GetCustomAttributes(), false).Select(a => " @" + a)))
                    .Append(')');
            }
            foreach (var attribute in Attributes(method.GetCustomAttributes(), false))
                builder.Append(" @").Append(attribute);
            return builder.ToString();
        }

        public string MethodRef(EntityHandle handle, GenericContext context) => handle.Kind switch
        {
            HandleKind.MethodDefinition => MethodDefRef((MethodDefinitionHandle)handle, context),
            HandleKind.MemberReference => MemberRef((MemberReferenceHandle)handle, context),
            _ => "?" + handle.Kind,
        };

        private string MethodDefRef(MethodDefinitionHandle handle, GenericContext context)
        {
            var method = reader.GetMethodDefinition(handle);
            return DefName(method.GetDeclaringType()) + "::" + reader.GetString(method.Name)
                + Signature(method.DecodeSignature(this, context with { Method = method.GetGenericParameters() }));
        }

        private string MemberRef(MemberReferenceHandle handle, GenericContext context)
        {
            var member = reader.GetMemberReference(handle);
            var parent = member.Parent.Kind == HandleKind.MethodDefinition
                ? MethodDefRef((MethodDefinitionHandle)member.Parent, context)
                : TypeOf(member.Parent);
            var signature = member.GetKind() == MemberReferenceKind.Method
                ? Signature(member.DecodeMethodSignature(this, context))
                : member.DecodeFieldSignature(this, context);
            return parent + "::" + reader.GetString(member.Name) + signature;
        }

        public string AccessorName(MethodDefinitionHandle handle) =>
            handle.IsNil ? "-" : reader.GetString(reader.GetMethodDefinition(handle).Name);

        public string ConstantOf(ConstantHandle handle)
        {
            if (handle.IsNil)
                return string.Empty;
            var constant = reader.GetConstant(handle);
            return Invariant($" ={constant.TypeCode}:{Convert.ToHexStringLower(reader.GetBlobBytes(constant.Value))}");
        }

        public static string Signature(MethodSignature<string> signature) =>
            Invariant($"{signature.Header}`{signature.GenericParameterCount}({string.Join(",", signature.ParameterTypes)})->{signature.ReturnType}/req{signature.RequiredParameterCount}");

        public string PropertySignature(MethodSignature<string> signature) => Signature(signature);

        // ── ISignatureTypeProvider ──
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle handle, byte rawTypeKind) =>
            (rawTypeKind == 0x11 ? "valuetype " : string.Empty) + DefName(handle);
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle handle, byte rawTypeKind) =>
            (rawTypeKind == 0x11 ? "valuetype " : string.Empty) + RefName(handle);
        public string GetTypeFromSpecification(MetadataReader r, GenericContext context, TypeSpecificationHandle handle, byte rawTypeKind) =>
            r.GetTypeSpecification(handle).DecodeSignature(this, context);
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) =>
            Invariant($"{elementType}[{shape.Rank};{string.Join(",", shape.Sizes)};{string.Join(",", shape.LowerBounds)}]");
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPinnedType(string elementType) => elementType + " pinned";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericTypeParameter(GenericContext genericContext, int index) =>
            "!" + index.ToString(CultureInfo.InvariantCulture);
        public string GetGenericMethodParameter(GenericContext genericContext, int index) =>
            "!!" + index.ToString(CultureInfo.InvariantCulture);
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr " + Signature(signature);
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
            unmodifiedType + (isRequired ? " modreq(" : " modopt(") + modifier + ")";
    }
}
