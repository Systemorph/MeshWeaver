using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.Json;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// A diagnostic must degrade to a diagnostic, never to a lost delivery.
///
/// <para><c>PolymorphicTypeInfoResolver.WarnMissingDerivedRegistrations</c> exists only to WARN about
/// polymorphic subtypes that are not registered on the hub, and to find them it enumerates the base
/// type's assembly with <c>Assembly.GetTypes()</c>. When ONE type in that assembly cannot be loaded —
/// a module whose dependency closure lacks an assembly (Plugins CI run 33595986160 and Reinsurance
/// main: a closure without <c>Microsoft.Agents.AI</c>) — <c>GetTypes()</c> throws
/// <see cref="ReflectionTypeLoadException"/>, the exception escapes <c>GetTypeInfo</c>, and the
/// serialization it was resolving for fails. The measured victim was
/// <c>MessageService.ReportFailure</c>: it could not serialize the <c>DeliveryFailure</c> envelope, so
/// the sender of the failed <c>CreateNodeRequest</c> never learned its request had failed.</para>
///
/// <para>The fixture reproduces the exact CLR condition without Roslyn: a probe module emitted with
/// <see cref="PersistedAssemblyBuilder"/> carries two loadable classes and one whose base type lives
/// in an assembly the module's load context cannot resolve. Serializing a loadable type from that
/// module must succeed, and the unloadable remainder must surface as ONE warning per assembly
/// naming the assembly and the missing dependency.</para>
/// </summary>
public class PolymorphicResolverUnloadableAssemblyTest(ITestOutputHelper output) : HubTestBase(output)
{
    [Fact]
    public void SerializationSurvivesAnAssemblyWhoseTypesDoNotAllLoad_AndWarnsOncePerAssembly()
    {
        var probe = LoadProbeModuleWithMissingDependency();

        // The fixture must reproduce the production condition, or a green below proves nothing.
        Action scan = () => probe.Loadable.Assembly.GetTypes();
        scan.Should().Throw<ReflectionTypeLoadException>(
            "the probe module must carry a type whose base lives in an assembly its load context cannot resolve");

        var typeRegistry = GetHost().ServiceProvider.GetRequiredService<ITypeRegistry>();
        var log = new ConcurrentQueue<(LogLevel Level, string Message)>();
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = new PolymorphicTypeInfoResolver(typeRegistry, "probe-hub", new CapturingLogger(log))
        };

        var instance = Activator.CreateInstance(probe.Loadable)!;
        probe.Loadable.GetProperty("Value")!.SetValue(instance, "hello");

        // Before the fix this threw ReflectionTypeLoadException out of GetTypeInfo: the envelope was
        // never written, and ReportFailure's DeliveryFailure post died in the same way.
        var json = JsonSerializer.Serialize(instance, probe.Loadable, options);
        json.Should().Contain("\"Value\":\"hello\"");

        var unloadable = log.Where(e => e.Message.Contains(probe.MissingDependency)).ToList();
        unloadable.Should().ContainSingle(
            "the unloadable remainder is reported once per assembly, naming the missing dependency");
        unloadable[0].Level.Should().Be(LogLevel.Warning);
        unloadable[0].Message.Should().Contain(probe.ModuleName,
            "the warning must name the assembly that could not be fully loaded");

        // A second type from the SAME assembly: the loadable-type scan is computed once per assembly
        // per resolver, so the diagnostic does not repeat per serialized type.
        var sibling = Activator.CreateInstance(probe.Sibling)!;
        JsonSerializer.Serialize(sibling, probe.Sibling, options).Should().StartWith("{");
        log.Count(e => e.Message.Contains(probe.MissingDependency)).Should().Be(1,
            "a per-type repeat of the per-assembly diagnostic would be a log storm on a busy hub");
    }

    private sealed record ProbeModule(Type Loadable, Type Sibling, string ModuleName, string MissingDependency);

    /// <summary>
    /// Emits a dependency assembly (one public class), then a probe module with two self-contained
    /// classes and one class deriving from the dependency's class, and loads the probe module into a
    /// context that cannot resolve the dependency — so <c>GetTypes()</c> on it throws
    /// <see cref="ReflectionTypeLoadException"/> with exactly one <c>null</c> slot.
    /// </summary>
    private static ProbeModule LoadProbeModuleWithMissingDependency()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var dependencyName = $"MissingDependency_{suffix}";
        var moduleName = $"ProbeModule_{suffix}";

        var dependencyBuilder = new PersistedAssemblyBuilder(new AssemblyName(dependencyName), typeof(object).Assembly);
        dependencyBuilder.DefineDynamicModule(dependencyName)
            .DefineType("Dependency.DependencyBase", TypeAttributes.Public | TypeAttributes.Class)
            .CreateType();
        using var dependencyStream = new MemoryStream();
        dependencyBuilder.Save(dependencyStream);
        dependencyStream.Position = 0;

        // The dependency is loaded into a THROWAWAY context only so the probe module can be emitted
        // against a runtime Type. The context the probe module is loaded into below never consults
        // this one (custom contexts resolve through their own Load, then the Default context), so
        // from the probe module's point of view the dependency does not exist.
        var emitContext = new AssemblyLoadContext($"emit-{suffix}", isCollectible: true);
        var dependencyBase = emitContext.LoadFromStream(dependencyStream).GetType("Dependency.DependencyBase")!;

        var moduleBuilder = new PersistedAssemblyBuilder(new AssemblyName(moduleName), typeof(object).Assembly);
        var module = moduleBuilder.DefineDynamicModule(moduleName);
        DefineClassWithStringProperty(module, "Probe.Loadable", "Value");
        DefineClassWithStringProperty(module, "Probe.Sibling", "Value");
        module.DefineType("Probe.Unloadable", TypeAttributes.Public | TypeAttributes.Class, dependencyBase).CreateType();
        using var moduleStream = new MemoryStream();
        moduleBuilder.Save(moduleStream);
        moduleStream.Position = 0;

        var runContext = new AssemblyLoadContext($"run-{suffix}", isCollectible: true);
        var probeAssembly = runContext.LoadFromStream(moduleStream);
        return new ProbeModule(
            probeAssembly.GetType("Probe.Loadable")!,
            probeAssembly.GetType("Probe.Sibling")!,
            moduleName,
            dependencyName);
    }

    private static void DefineClassWithStringProperty(ModuleBuilder module, string typeName, string propertyName)
    {
        var typeBuilder = module.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class);
        var field = typeBuilder.DefineField($"_{propertyName}", typeof(string), FieldAttributes.Private);
        var property = typeBuilder.DefineProperty(propertyName, PropertyAttributes.None, typeof(string), null);
        const MethodAttributes accessorAttributes =
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
        var getter = typeBuilder.DefineMethod($"get_{propertyName}", accessorAttributes, typeof(string), Type.EmptyTypes);
        var getterIl = getter.GetILGenerator();
        getterIl.Emit(OpCodes.Ldarg_0);
        getterIl.Emit(OpCodes.Ldfld, field);
        getterIl.Emit(OpCodes.Ret);
        var setter = typeBuilder.DefineMethod($"set_{propertyName}", accessorAttributes, null, [typeof(string)]);
        var setterIl = setter.GetILGenerator();
        setterIl.Emit(OpCodes.Ldarg_0);
        setterIl.Emit(OpCodes.Ldarg_1);
        setterIl.Emit(OpCodes.Stfld, field);
        setterIl.Emit(OpCodes.Ret);
        property.SetGetMethod(getter);
        property.SetSetMethod(setter);
        typeBuilder.CreateType();
    }

    private sealed class CapturingLogger(ConcurrentQueue<(LogLevel Level, string Message)> sink) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => sink.Enqueue((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
