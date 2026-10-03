using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh.Storage;
using MeshWeaver.Mesh.Threading;

namespace MeshWeaver.Graph.Storage;

/// <summary>
/// A pre-configured store whose containers are the DIRECTORIES under one file-system root — the
/// shape a file-system-backed instance (a local or monolith mesh) keeps its content in. Listing and
/// creating go through the <see cref="IoPoolNames.FileSystem"/> pool.
/// </summary>
public sealed partial class DirectoryInstanceStore : IInstanceStore
{
    private readonly string root;
    private readonly IIoPool pool;

    /// <summary>Creates the store.</summary>
    /// <param name="id">The store id.</param>
    /// <param name="displayName">A human-readable name (shows the root's last segment, never more).</param>
    /// <param name="root">The directory whose sub-directories are the containers.</param>
    /// <param name="defaultPurposes">The purposes it serves when nothing is bound.</param>
    /// <param name="pools">The mesh's I/O pools.</param>
    public DirectoryInstanceStore(
        string id, string displayName, string root, IEnumerable<string> defaultPurposes, IoPoolRegistry pools)
    {
        Id = id;
        DisplayName = displayName;
        this.root = root;
        DefaultPurposes = defaultPurposes.ToImmutableHashSet(StringComparer.Ordinal);
        pool = pools.Get(IoPoolNames.FileSystem);
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string Kind => StorageStoreKind.Directory;

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public IReadOnlySet<string> DefaultPurposes { get; }

    /// <inheritdoc />
    public string DefaultContainerFor(string partition) => partition;

    /// <inheritdoc />
    public string? ValidateName(string name)
        => ValidName().IsMatch(name)
            ? null
            : $"'{name}' is not a valid directory name here: 1-63 characters, letters, digits, '.', '_' or '-', starting with a letter or digit.";

    /// <inheritdoc />
    public IObservable<ImmutableList<string>> ListContainers()
        => pool.InvokeBlocking(_ => System.IO.Directory.Exists(root)
            ? System.IO.Directory.GetDirectories(root)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .ToImmutableList()
            : ImmutableList<string>.Empty);

    /// <inheritdoc />
    public IObservable<StorageProbe> EnsureContainer(string name)
    {
        if (ValidateName(name) is { } reason)
            return Observable.Return(StorageProbe.Refused(name, reason));
        return pool.InvokeBlocking(_ =>
        {
            var path = Path.Combine(root, name);
            if (System.IO.Directory.Exists(path))
                return new StorageProbe(name, Reachable: true, Exists: true, Created: false);
            System.IO.Directory.CreateDirectory(path);
            return new StorageProbe(name, Reachable: true, Exists: true, Created: true);
        }).Catch((Exception ex) => Observable.Return(StorageProbe.Unreachable(name, ex.Message)));
    }

    /// <inheritdoc />
    public IObservable<StorageProbe> Probe(string name)
    {
        if (ValidateName(name) is { } reason)
            return Observable.Return(StorageProbe.Refused(name, reason));
        return pool.InvokeBlocking(_ => System.IO.Directory.Exists(root)
                ? new StorageProbe(name, Reachable: true, Exists: System.IO.Directory.Exists(Path.Combine(root, name)), Created: false)
                : StorageProbe.Unreachable(name, "the store's root directory does not exist"))
            .Catch((Exception ex) => Observable.Return(StorageProbe.Unreachable(name, ex.Message)));
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$")]
    private static partial Regex ValidName();
}
