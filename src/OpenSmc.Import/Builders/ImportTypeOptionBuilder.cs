using System.Linq.Expressions;
using OpenSmc.DataStructures;

namespace OpenSmc.Import.Builders;

public interface IImportTypeOptionBuilder
{
    IImportTypeOptionBuilder SnapshotMode();
}

//this is needed to disable pass into WithType function with only default mappings
public interface IImportMappingTypeOptionBuilder<T> : IImportTypeOptionBuilder
{
    IImportMappingTypeOptionBuilder<T> MapProperty<TProperty>(Expression<Func<T, TProperty>> selector, Expression<Func<IDataSet, IDataRow, TProperty>> propertyMapping);
    new IImportMappingTypeOptionBuilder<T> SnapshotMode();//this one must return same type, otherwise after calling of this method we can not call other methods
}