using System;
using MeshWeaver.Courses.Configuration;
using Xunit;

namespace MeshWeaver.Courses.Test;

/// <summary>
/// Pure unit tests of <see cref="ExerciseAttemptNodeType.AttemptPathFor"/> —
/// the ONE static mapping from an exercise path
/// (<c>{coursePath}/{moduleId}/Exercise/{exerciseId}</c>) to the trainee's
/// attempt path
/// (<c>{userHome}/Courses/{Escape(coursePath)}/{moduleId}/{exerciseId}</c>).
/// </summary>
public class AttemptPathMappingTest
{
    [Fact]
    public void TopLevelCourse_MapsToEscapedAttemptPath()
    {
        ExerciseAttemptNodeType
            .AttemptPathFor("roland", "AgenticEngineering/Introduction/Exercise/Ex1")
            .Should().Be("roland/Courses/AgenticEngineering/Introduction/Ex1");
    }

    [Fact]
    public void NestedCourse_FlattensCoursePathWithEscape()
    {
        // Course lives inside a partition: the whole course path is flattened
        // into ONE segment via PathEscaping.Escape ("/" → "__").
        ExerciseAttemptNodeType
            .AttemptPathFor("roland", "acme/Training/MyCourse/Module2/Exercise/Ex3")
            .Should().Be("roland/Courses/acme__Training__MyCourse/Module2/Ex3");
    }

    [Fact]
    public void DeepUserHome_IsUsedVerbatim()
    {
        ExerciseAttemptNodeType
            .AttemptPathFor("TestUser", "rbuergi/Course1/Mod1/Exercise/1")
            .Should().Be("TestUser/Courses/rbuergi__Course1/Mod1/1");
    }

    [Fact]
    public void PathWithoutExerciseSegment_Throws()
    {
        Action act = () => ExerciseAttemptNodeType
            .AttemptPathFor("roland", "acme/MyCourse/Module1/Ex1");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TooShortPath_Throws()
    {
        // "Exercise/Ex1" has no course/module ancestry.
        Action act = () => ExerciseAttemptNodeType.AttemptPathFor("roland", "Exercise/Ex1");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EmptyUserHome_Throws()
    {
        Action act = () => ExerciseAttemptNodeType
            .AttemptPathFor("", "acme/MyCourse/Module1/Exercise/Ex1");
        act.Should().Throw<ArgumentException>();
    }
}
