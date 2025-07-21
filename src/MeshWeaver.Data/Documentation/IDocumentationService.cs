using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Xml.Serialization;
using MeshWeaver.Data.Documentation.Model;
using MeshWeaver.Messaging;
using Assembly = System.Reflection.Assembly;

namespace MeshWeaver.Data.Documentation;

public interface IDocumentationService
{
    DocumentationContext Context { get; }
    Stream? GetStream(string type, string dataSource, string documentName);
    DocumentationSource? GetSource(string type, string id);
    Member? GetDocumentation(MemberInfo member);
}

public class DocumentationService(IMessageHub hub) : IDocumentationService
{
    public DocumentationContext Context { get; } = hub.CreateDocumentationContext();

    public Stream? GetStream(string fullPath)
        => Context.Sources.Values
            .Select(s => s?.GetStream(fullPath))
            .FirstOrDefault(s => s is not null);

    public Stream? GetStream(string type, string dataSource, string documentName) => 
        Context.GetSource(type, dataSource)?
            .GetStream(documentName);


    public DocumentationSource? GetSource(string type, string id) =>
        Context.GetSource(type, id);

    public Member? GetDocumentation(MemberInfo member)
    {
        try
        {
            var assembly = (member as Type ?? member.DeclaringType)!.Assembly;
            var members = docsByAssembly.GetOrAdd(assembly, GetAssembly);
            var name = member switch
            {
                Type t => $"T:{t.FullName}",
                PropertyInfo p => $"P:{p.DeclaringType!.FullName}.{p.Name}",
                MethodInfo m => $"M:{m.DeclaringType!.FullName}.{m.Name}({string.Join(',',m.GetParameters().Select(p =>p.ParameterType.FullName))})",
                FieldInfo f => $"F:{f.DeclaringType!.FullName}.{f.Name}",
                EventInfo e => $"E:{e.DeclaringType!.FullName}.{e.Name}",
                _ => null
            };
            if (name == null)
                return null;
            if (members?.TryGetValue(name, out var ret) == false)
                return ret;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private IReadOnlyDictionary<string, Member>? GetAssembly(Assembly assembly)
    {
        try
        {
            if (string.IsNullOrEmpty(assembly.Location))
                return null;
            var assemblyName = assembly.GetName().Name!;
            var source = GetSource(EmbeddedDocumentationSource.Embedded, assemblyName);
            var stream = source?.GetStream($"{assemblyName}.xml");
            if (stream is null)
                return null;
            return Deserialize(stream).Members?.ToDictionary(m => m.Name!) ??
                   (IReadOnlyDictionary<string, Member>)ImmutableDictionary<string, Member>.Empty;
        }
        catch
        {
            return ImmutableDictionary<string,Member>.Empty;
        }
    }

    private Doc Deserialize(Stream stream)
    {

        var serializer = new XmlSerializer(typeof(Doc));
        return (Doc)serializer.Deserialize(stream)!;
    }

    private readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<string, Member>?> docsByAssembly = new();
}
