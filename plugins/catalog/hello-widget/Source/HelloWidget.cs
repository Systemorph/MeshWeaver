// A sample runtime-compiled NodeType shipped as a code package. The mesh discovers this Code node
// under the NodeType's Source/ subtree, compiles it with Roslyn on install, and serves nodes of
// type "hello-widget" live — no app rebuild, no NuGet.
public record HelloWidget
{
    public string Title { get; init; } = "Hello from a runtime-compiled plugin";

    public string Body { get; init; } = string.Empty;
}
