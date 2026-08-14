# MeshWeaver.Education

The education feature pack: the compiled half of the course/learning experience that cannot live
as in-mesh plugin content — the course navigation contributor (`EducationNavigationProvider`,
claimed by node shape over the `INodeNavigationProvider` seam), the learner-copy / course-shell
layout areas (`EducationLayoutAreas`: `StartExercise`, `GoToMyCopy`, `CourseNav`, `Learn`), visit
tracking (`CourseProgress`), and the training-sim thread adapter (`TrainingSimResponder`).

Course *content* — lessons, modules, exercises, quizzes — ships as the `Edu` plugin
(node-native, from the plugins registry); this package is the runtime it plugs into. Extracted
from `MeshWeaver.Graph`/`MeshWeaver.AI` per the
[UI Extensibility](https://github.com/Systemorph/MeshWeaver/blob/main/src/MeshWeaver.Documentation/Data/Architecture/UiExtensibility.md)
lanes: a deployment that serves no courses simply doesn't register it.

```csharp
builder.AddEducationNavigation();          // mesh builder: the course left-rail contributor
config.AddLayout(l => l.AddEducationLayoutAreas());   // node-hub config: the course areas
```

Part of [MeshWeaver](https://github.com/Systemorph/MeshWeaver).
