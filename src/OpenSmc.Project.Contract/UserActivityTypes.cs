namespace OpenSmc.Project.Contract;

public static class UserActivityTypes
{
    public const string OpenProject = nameof(OpenProject);
    public const string ProjectEdit = nameof(ProjectEdit);
    public const string OpenNotebook = nameof(OpenNotebook);
    public const string RunSmapp = nameof(RunSmapp);
    public const string AttachSmapp = nameof(AttachSmapp);
    public const string NotebookEdit = nameof(NotebookEdit);
    public const string SessionEvent = nameof(SessionEvent);
    public const string CloneProject = nameof(CloneProject);
    public const string CreateProject = nameof(CreateProject);

    public const string SessionStartRequested = nameof(SessionStartRequested);
    public const string SessionStopRequested = nameof(SessionStopRequested);
    public const string SessionPodStarted = nameof(SessionPodStarted);
    public const string SessionStarted = nameof(SessionStarted);
    public const string SessionInitialized = nameof(SessionInitialized);
    public const string SessionStopped = nameof(SessionStopped);
    public const string SessionAutoStartRequested = nameof(SessionAutoStartRequested);
}