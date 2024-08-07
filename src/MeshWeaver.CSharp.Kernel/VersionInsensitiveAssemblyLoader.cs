using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.CSharp.Kernel;

public static class VersionInsensitiveAssemblyLoader
{
        [ModuleInitializer]
        public static void SubscribeToAssemblyResolve()
        {
            AssemblyLoadContext.Default.Resolving += OnDefaultOnResolving;
        }

        private static Assembly OnDefaultOnResolving(AssemblyLoadContext context, AssemblyName assemblyName)
        {
            var assemblyShortName = assemblyName.Name!;
            var res = LoadFromDefaultContext(assemblyShortName);
            return res;
        }

        public static Assembly LoadFromDefaultContext(string shortAssemblyName)
        {
            var res = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(x => x.GetName().Name == shortAssemblyName);
            return res;
        }

        public static Assembly LoadAssembly(string path, ILogger logger)
        {
            try
            {
                // TODO V10: Read dll metadata with dnlib (2022.03.16, Yury Pekishev)
                var assemblyShortName = Path.GetFileNameWithoutExtension(path);
                var assembly = LoadFromDefaultContext(assemblyShortName);
                return assembly ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }
            catch (Exception exc)
            {
                logger.LogDebug("Could not resolve Assembly {path}: {error}", path, exc.Message);
                return null;
            }
        }

        public static Assembly LoadAssembly(AssemblyName assemblyName, ILogger logger)
        {
            try
            {
                var assemblyShortName = assemblyName.Name!;
                var assembly = LoadFromDefaultContext(assemblyShortName);
                if (assembly != null)
                    LogVersionIfMismatch(assemblyShortName, assembly.GetName().Version, assemblyName.Version, logger);
                return assembly ?? AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
            }
            catch (Exception exc)
            {
                logger.LogDebug("Could not load Assembly {path}: {error}", assemblyName, exc.Message);
                return null;
            }
        }

        private static void LogVersionIfMismatch(string assemblyShortName, Version actual, Version requested, ILogger logger)
        {
            switch (actual.CompareTo(requested))
            {
                case 1:
                {
                    logger.LogInformation("Assembly '{name}' version mismatch: '{existing}' was loaded before, cannot load '{requested}' over", assemblyShortName, actual, requested);
                    return;
                }
                case -1:
                {
                    logger.LogWarning("Assembly '{name}' version mismatch: '{existing}' was loaded before, cannot load '{requested}' over", assemblyShortName, actual, requested);
                    return;
                }
            }
        }
}