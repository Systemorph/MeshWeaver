namespace MeshWeaver.Project.Contract;

public abstract record UserSettings(string Id, int Version) ;

public record ProjectUserSettings(string Id, int Version, string DefaultEnvironment) : UserSettings(Id, Version);