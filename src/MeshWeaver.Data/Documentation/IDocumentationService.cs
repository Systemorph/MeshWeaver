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
    Stream GetStream(string type, string dataSource, string documentName);
    DocumentationSource GetSource(string type, string id);
    Member GetDocumentation(MemberInfo member);
}

public class DocumentationService(IMessageHub hub) : IDocumentationService
{
    public DocumentationContext Context { get; } = hub.CreateDocumentationContext();

    public Stream GetStream(string fullPath)
        => Context.Sources.Values
            .Select(s => s.GetStream(fullPath))
            .FirstOrDefault(s => s != null);

    public Stream GetStream(string type, string dataSource, string documentName) => 
        Context.GetSource(type, dataSource)?
            .GetStream(documentName);


    public DocumentationSource GetSource(string type, string id) =>
        Context.GetSource(type, id);

    public Member GetDocumentation(MemberInfo member)
    {
        try
        {
            var assembly = (member as Type ?? member.DeclaringType)!.Assembly;
            var members = docsByAssembly.GetOrAdd(assembly, GetAssembly);
            if (members == null)
                return null;
            var name = member switch
            {
                Type t => $"T:{t.FullName}",
                PropertyInfo p => $"P:{p.DeclaringType!.FullName}.{p.Name}",
                MethodInfo m => $"M:{m.DeclaringType!.FullName}.{m.Name}",
                FieldInfo f => $"F:{f.DeclaringType!.FullName}.{f.Name}",
                EventInfo e => $"E:{e.DeclaringType!.FullName}.{e.Name}",
                _ => null
            };
            if (name == null || !members.TryGetValue(name, out var ret))
                return null;
            return ret;

        }
        catch
        {
            return null;
        }
    }

    private IReadOnlyDictionary<string, Member> GetAssembly(Assembly assembly)
    {
        try
        {
            var assemblyName = assembly.GetName().Name;
            var source = GetSource(EmbeddedDocumentationSource.Embedded, assemblyName);
            var stream = source.GetStream($"{assemblyName}.xml");
            return Deserialize(stream)?.Members?.ToDictionary(m => m.Name) ??
                   (IReadOnlyDictionary<string, Member>)ImmutableDictionary<string, Member>.Empty;
        }
        catch
        {
            return ImmutableDictionary<string,Member>.Empty;
        }
    }

    private Doc Deserialize(Stream stream)
    {
        if (stream == null)
            return null;

        var serializer = new XmlSerializer(typeof(Doc));
        return (Doc)serializer.Deserialize(stream);
    }

    private readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<string, Member>> docsByAssembly = new();
}
