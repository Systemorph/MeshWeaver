using System.Reflection;
using Microsoft.EntityFrameworkCore;
using MeshWeaver.Data;
using MeshWeaver.Reflection;

namespace MeshWeaver.DataStorage.EntityFramework;

public record EntityFrameworkUnpartitionedDataSource(
    object Id,
    IWorkspace Workspace,
    EntityFrameworkDataStorage EntityFrameworkDataStorage
) : UnpartitionedDataSourceWithStorage<EntityFrameworkUnpartitionedDataSource,ITypeSource>(Id, Workspace, EntityFrameworkDataStorage)
{
    public override void Initialize()
    {
        EntityFrameworkDataStorage.Initialize(ModelBuilder ?? ConvertDataSourceMappings);
        base.Initialize();
    }

    public EntityFrameworkUnpartitionedDataSource WithModel(Action<ModelBuilder> modelBuilder) =>
        this with
        {
            ModelBuilder = modelBuilder
        };

    public Action<ModelBuilder> ModelBuilder { get; init; }

    private void ConvertDataSourceMappings(ModelBuilder builder)
    {
        foreach (var type in MappedTypes)
            builder.Model.AddEntityType(type);
    }

    public override EntityFrameworkUnpartitionedDataSource WithType(
        Type type,
        Func<ITypeSource, ITypeSource> config
    ) => (EntityFrameworkUnpartitionedDataSource)WithTypeMethod.MakeGenericMethod(type).InvokeAsFunction(config);

    private static readonly MethodInfo WithTypeMethod =
        ReflectionHelper.GetMethodGeneric<EntityFrameworkUnpartitionedDataSource>(x => x.WithType<object>(null));

    
    public override EntityFrameworkUnpartitionedDataSource WithType<T>(
        Func<ITypeSource, ITypeSource> typeSource
    )
        where T : class =>
        WithType<T>(x => (TypeSourceWithTypeWithDataStorage<T>)typeSource.Invoke(x));

    public EntityFrameworkUnpartitionedDataSource WithType<T>(
        Func<TypeSourceWithTypeWithDataStorage<T>, TypeSourceWithTypeWithDataStorage<T>> typeSource
    )
        where T : class =>
        this with
        {
            TypeSources = TypeSources.Add(
                typeof(T),
                typeSource.Invoke(new TypeSourceWithTypeWithDataStorage<T>(Workspace, Id, Storage))
            )
        };
}
