namespace MeshWeaver.Courses;

/// <summary>
/// Content of an <c>Exercise</c> MeshNode — a "your turn" task inside a
/// module. The exercise's code artifacts are plain <c>Code</c> children:
/// <c>{exercise}/Source/Starter</c> (the trainee's starting point),
/// <c>{exercise}/Test/Validation</c> (instructor unit tests — the spec) and
/// <c>{exercise}/Solution/Solution</c> (the reference solution).
/// </summary>
public record ExerciseConfiguration
{
    /// <summary>
    /// The exercise statement presented to the trainee (markdown allowed).
    /// </summary>
    public string? Statement { get; init; }

    /// <summary>
    /// Relative difficulty of the exercise within its module (1 = easiest).
    /// Used for ordering and display; carries no execution semantics.
    /// </summary>
    public int Difficulty { get; init; } = 1;

    /// <summary>
    /// The programming language of the exercise's code artifacts. Mirrors
    /// <c>CodeConfiguration.Language</c>; defaults to <c>"csharp"</c>.
    /// </summary>
    public string Language { get; init; } = "csharp";
}
