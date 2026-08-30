using MeshWeaver.Plugin.Build;

// 🚨 NO NuGet ANYWHERE IN THIS TOOL. Until 2026-08-30 the bare verb packed a node package by
// emitting a csproj that referenced the MeshWeaver.* NuGet packages at a "floor" (the newest
// version every package was published at) and ran `dotnet build` on it. That floor stopped at
// 3.0.0-rc7 the day MeshWeaver.AI and MeshWeaver.Markdown.Collaboration left the platform repo, so
// every plugin using an rc8 type failed to pack, and the proposed cure was a NuGet publish token.
// The maintainer's rule: in-mesh source runs INSIDE the portal image, so the only honest reference
// set is that image's assemblies plus the modules its publication was sealed against — which is
// exactly what node-repo-compile-check.yml and the gates compile against. Nothing a node repo
// builds goes to a package feed; the registry serves BUNDLES assembled from sealed publications.
if (args.Length > 0 && args[0] == ModulePackCommand.Verb)
    return ModulePackCommand.Run(args.Skip(1).ToArray());

if (args.Length > 0 && args[0] == ModuleFetchCommand.Verb)
    return ModuleFetchCommand.Run(args.Skip(1).ToArray());

Console.Error.WriteLine("""
    usage: meshweaver-plugin-build module-pack <moduleOutputDir> [options]   (see module-pack --help)
           meshweaver-plugin-build module-fetch <package> [options]          (see module-fetch --help)

    The node-package pack verb (NuGet-floor compile + .nupkg) was retired on 2026-08-30: in-mesh
    source is type-checked against the platform IMAGE by node-repo-compile-check.yml, and packages
    reach consumers as bundles from a sealed publication, never from a package feed.
    """);
return args.Length == 0 || args[0] is "-h" or "--help" ? 0 : 2;
