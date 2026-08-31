namespace MeshWeaver.Cli;

/// <summary>
/// <c>memex build project &lt;csproj|dir&gt; [--image &lt;image&gt;]</c> — compile a .NET project with
/// NO dotnet SDK and NO NuGet restore, against the assemblies of a MeshWeaver image (maintainer,
/// 2026-08-30: <i>"the platform builds dll completely without any external dotnet kit or
/// nuget"</i>).
///
/// <para><b>This half is only the trip into the container.</b> The work — evaluating the
/// <c>.csproj</c> without MSBuild, resolving every reference from <c>/app</c> and the image's own
/// <c>.deps.json</c>, sequencing the <c>ProjectReference</c> graph and running Roslyn — is
/// <c>mw-plugin-test build-project</c>, which is already in the image. It shares
/// <see cref="ImageRunner"/> with <see cref="BuildPluginCommand"/> rather than carrying a second
/// copy of the pull retry and the digest pin.</para>
///
/// <para><b>Two ways to run.</b> With <c>--image</c> the tool pulls that image, pins it by digest
/// and runs the builder inside it. Without <c>--image</c> it runs the builder that is already
/// beside it — the case where this command is itself executing inside a MeshWeaver image. There is
/// no third way, and in particular no "just use the local SDK" fallback: the whole point is that
/// the reference set is a container's, so a run that cannot name a container fails.</para>
///
/// <para><b>What gets mounted.</b> <c>/repo</c> is the SOURCE ROOT — the directory of the nearest
/// <c>Directory.Build.props</c> above the project, or the project's own directory when there is
/// none. That is the same boundary the builder uses to decide which <c>ProjectReference</c>s are
/// its to build and which are the container's to supply, so mounting anything narrower would
/// silently change the answer. <c>--root</c> overrides it when a repo's import chain reaches
/// further up.</para>
/// </summary>
public sealed class BuildProjectCommand(TextWriter output, TextWriter error)
{
    private readonly ImageRunner _runner = new(output, error);

    /// <summary>Where the builder lives inside every MeshWeaver image.</summary>
    public const string InImageBuilder = "/app/mw-plugin-test";

    /// <summary>The verb the in-image builder is invoked with.</summary>
    public const string BuilderVerb = "build-project";

    /// <summary>Runs the build.</summary>
    /// <param name="options">What to build and how.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The builder's exit code, or a usage/plumbing code (2, 4, 5).</returns>
    public async Task<int> RunAsync(BuildProjectOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var entry = Path.GetFullPath(options.ProjectPath);
        if (!File.Exists(entry) && !Directory.Exists(entry))
        {
            await error.WriteLineAsync($"error: '{entry}' is neither a project file nor a directory.");
            return 2;
        }

        var projectDirectory = File.Exists(entry) ? Path.GetDirectoryName(entry)! : entry;
        var root = options.SourceRoot is { Length: > 0 } explicitRoot
            ? Path.GetFullPath(explicitRoot)
            : FindSourceRoot(projectDirectory);
        if (!entry.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entry, root, StringComparison.OrdinalIgnoreCase))
        {
            await error.WriteLineAsync(
                $"error: the project '{entry}' is not inside the source root '{root}', so mounting the "
                + "root would not carry it into the container. Pass --root with a directory that "
                + "contains both the project and its Directory.Build.props chain.");
            return 2;
        }

        var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
        var outputDirectory = options.Output is { Length: > 0 } o
            ? Path.GetFullPath(o)
            : Path.Combine(Path.GetTempPath(), $"memex-build-project-{Environment.ProcessId}");
        Directory.CreateDirectory(outputDirectory);
        // The image runs as a non-root user, so an output mount it cannot write is a promise this
        // command cannot keep (the Manufacturing #37 failure, applied to a caller-named directory).
        ImageRunner.MakeContainerWritable(outputDirectory);

        var builderArgs = new List<string> { BuilderVerb, $"/repo/{relative}", "--output", "/out" };
        foreach (var accept in options.Accept)
        {
            builderArgs.Add("--accept");
            builderArgs.Add(accept);
        }
        if (options.AllowWarnings)
            builderArgs.Add("--allow-warnings");
        foreach (var extra in options.ExtraArgs)
            builderArgs.Add(extra);

        if (options.Image is not { Length: > 0 } image)
        {
            // No image named: the only remaining honest reference set is the one this process is
            // already standing in. If the builder is not beside us, say which of the two modes the
            // caller meant rather than falling back to a local SDK build that would answer a
            // different question.
            if (!File.Exists(InImageBuilder))
            {
                await error.WriteLineAsync(
                    $"error: no --image was given and '{InImageBuilder}' is not here, so there is no "
                    + "container to build against. Pass --image <image> to run the build inside one; "
                    + "omitting it is only valid when this command is itself running inside a "
                    + "MeshWeaver image. There is deliberately no local-SDK fallback — the reference "
                    + "set IS the container.");
                return 4;
            }
            var local = new List<string> { BuilderVerb, entry, "--output", outputDirectory };
            local.AddRange(builderArgs.Skip(4));
            return await _runner.Exec(InImageBuilder, local, ct);
        }

        var pinned = options.NoPull
            ? await _runner.UseLocalImage(image, ct)
            : await _runner.PullImage(image, ct);
        if (pinned is null) return 4;
        await output.WriteLineAsync($"image: {pinned}");
        await output.WriteLineAsync($"source root: {root} → /repo; project: /repo/{relative}");

        var mounts = new List<string> { $"{root}:/repo:ro", $"{outputDirectory}:/out" };
        foreach (var dir in options.ExtraReferenceDirectories.Select(Path.GetFullPath))
        {
            if (!Directory.Exists(dir))
            {
                await error.WriteLineAsync(
                    $"error: --extra-refs '{dir}' does not exist. An additional-library directory that "
                    + "is not there would silently supply nothing.");
                return 2;
            }
            var mountPoint = $"/refs/{Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar))}";
            mounts.Add($"{dir}:{mountPoint}:ro");
            builderArgs.Add("--extra-refs");
            builderArgs.Add(mountPoint);
        }

        var exit = await _runner.RunInImage(pinned, mounts, [], builderArgs, ct);
        if (exit == 0)
            await output.WriteLineAsync($"assemblies: {outputDirectory}");
        return exit;
    }

    /// <summary>
    /// The nearest <c>Directory.Build.props</c> ancestor's directory, or <paramref name="start"/>
    /// when no ancestor carries one. The same walk MSBuild performs, and the same one the in-image
    /// builder performs — the two must agree or the mount and the build disagree about which
    /// projects are ours.
    /// </summary>
    /// <param name="start">The project's directory.</param>
    /// <returns>The source root.</returns>
    public static string FindSourceRoot(string start)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(start);
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Path.GetFullPath(start);
    }
}

/// <summary>Arguments for <see cref="BuildProjectCommand"/>. A record so a caller cannot half-set it.</summary>
public sealed record BuildProjectOptions
{
    /// <summary>The <c>.csproj</c> to build, or a directory holding exactly one.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>Image to build against; omitted only when already running inside one.</summary>
    public string? Image { get; init; }

    /// <summary>Use the image the docker daemon already has instead of pulling it — for an image
    /// built locally, which no registry can serve.</summary>
    public bool NoPull { get; init; }

    /// <summary>Where the emitted assemblies land on the host. Default: a temp directory.</summary>
    public string? Output { get; init; }

    /// <summary>Overrides the mounted source root when a repo's import chain reaches above it.</summary>
    public string? SourceRoot { get; init; }

    /// <summary>Host directories of libraries ADDITIONAL to the platform, mounted read-only.</summary>
    public IReadOnlyList<string> ExtraReferenceDirectories { get; init; } = [];

    /// <summary>Constructs the evaluator may not reproduce, acknowledged one by one.</summary>
    public IReadOnlyList<string> Accept { get; init; } = [];

    /// <summary>Opts out of the no-warn policy (<c>--no-warn=false</c> / <c>--allow-warnings</c>).</summary>
    public bool AllowWarnings { get; init; }

    /// <summary>Anything else to pass straight through to the in-image builder.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];
}
