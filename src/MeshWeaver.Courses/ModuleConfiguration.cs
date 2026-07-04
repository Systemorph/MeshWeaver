namespace MeshWeaver.Courses;

/// <summary>
/// Content of a <c>Module</c> MeshNode — one course module. Theory blocks are
/// plain <c>Markdown</c> children, worked examples plain <c>Code</c> children,
/// and exercises live under <c>{module}/Exercise/{n}</c> as <c>Exercise</c>
/// nodes.
/// </summary>
public record ModuleConfiguration
{
    /// <summary>
    /// Short summary of the module shown at the top of the module page and on
    /// course-level module cards (markdown allowed).
    /// </summary>
    public string? Summary { get; init; }
}
