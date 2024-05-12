using System.Reflection;
using Microsoft.EntityFrameworkCore;
using OpenSmc.Data;
using OpenSmc.Data.Serialization;
using OpenSmc.Messaging;
using OpenSmc.Reflection;

namespace OpenSmc.DataStorage.EntityFramework;

public record EntityFrameworkDataSource(
    object Id,
    IMessageHub Hub,
    EntityFrameworkDataStorage EntityFrameworkDataStorage
) : DataSourceWithStorage<EntityFrameworkDataSource>(Id, Hub, EntityFrameworkDataStorage)
{
    public override void Initialize()
    {
        EntityFrameworkDataStorage.Initialize(ModelBuilder ?? ConvertDataSourceMappings);
        base.Initialize();
    }

    public EntityFrameworkDataSource WithModel(Action<ModelBuilder> modelBuilder) =>
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

    public override EntityFrameworkDataSource WithType(
        Type type,
        Func<ITypeSource, ITypeSource> config
    ) => (EntityFrameworkDataSource)WithTypeMethod.MakeGenericMethod(type).InvokeAsFunction(config);

    private static readonly MethodInfo WithTypeMethod =
        ReflectionHelper.GetMethodGeneric<EntityFrameworkDataSource>(x => x.WithType<object>(null));

    protected override EntityFrameworkDataSource WithType<T>(
        Func<ITypeSource, ITypeSource> typeSource
    )
        where T : class =>
        WithType<T>(x => (TypeSourceWithTypeWithDataStorage<T>)typeSource.Invoke(x));

    public EntityFrameworkDataSource WithType<T>(
        Func<TypeSourceWithTypeWithDataStorage<T>, TypeSourceWithTypeWithDataStorage<T>> typeSource
    )
        where T : class =>
        this with
        {
            TypeSources = TypeSources.Add(
                typeof(T),
                typeSource.Invoke(new TypeSourceWithTypeWithDataStorage<T>(Hub, Id, Storage))
            )
        };
}
