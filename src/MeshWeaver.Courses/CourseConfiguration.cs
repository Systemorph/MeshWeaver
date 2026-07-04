namespace MeshWeaver.Courses;

/// <summary>
/// Content of a <c>Course</c> MeshNode — the root of an interactive course.
/// A course is NOT a Space (it does not own a partition), so courses can live
/// in any partition; modules are child <c>Module</c> nodes ordered by
/// <c>MeshNode.Order</c>.
/// </summary>
public record CourseConfiguration
{
    /// <summary>
    /// Human-readable course description shown on the course overview
    /// (markdown allowed).
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Instructions for the AI tutor agent working with trainees on this
    /// course (tone, hint policy, what never to reveal). Read by the Tutor
    /// agent from the course root; not rendered to trainees.
    /// </summary>
    public string? TutorInstructions { get; init; }
}
