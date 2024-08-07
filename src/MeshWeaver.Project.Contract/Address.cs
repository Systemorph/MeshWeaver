// ReSharper disable once CheckNamespace
namespace Systemorph.Notebook.Addresses;

public static class Address
{
    public static ProjectEnvironment Environment(string path) => new(path);
    public static Project Project(string path) => new(path);

}

public record ProjectEnvironment(string Path);
public record Project(string Path);
