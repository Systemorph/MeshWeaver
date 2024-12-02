using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using Autofac.Core.Activators.Reflection;
using MeshWeaver.Collections;
using MeshWeaver.Data;
using MeshWeaver.DataStructures;
using MeshWeaver.Domain;
using MeshWeaver.Reflection;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Import.Configuration;

public abstract record TableMapping(string Name)
{
    public abstract Task<EntityStore> Map(IDataSet dataSet, IDataTable dataSetTable, EntityStore store);
}

public record DirectTableMapping(
    string Name,
    Func<IDataSet, IDataTable, IWorkspace, EntityStore, Task<EntityStore>> MappingFunction,
    IWorkspace Workspace
) : TableMapping(Name)
{
    public override Task<EntityStore> Map(IDataSet dataSet, IDataTable dataSetTable, EntityStore store) =>
        MappingFunction(dataSet, dataSetTable, Workspace, store);
}

public record TableToTypeMapping(string Name, Type Type, IWorkspace Workspace) : TableMapping(Name)
{
    public override Task<EntityStore> Map(IDataSet dataSet, IDataTable dataSetTable, EntityStore store) =>
        Task.FromResult(MapTableToType(dataSet, dataSetTable, store));

    private EntityStore MapTableToType(IDataSet dataSet, IDataTable table, EntityStore store)
    {
        var constructor = InstanceInitFunction;
        if (constructor == null)
        {
            var init = GetInstanceInitFunction(
                Type,
                table
                    .Columns.Select((c, i) => new KeyValuePair<string, int>(c.ColumnName, i))
                    .ToDictionary()
            );
            constructor = (_, dr) => init(dr);
        }
        var instances = table.Rows.Select(r => constructor.Invoke(dataSet, r)).ToArray();
        return Workspace.AddInstances(store, instances);
    }

    private Func<IDataSet, IDataRow, object> InstanceInitFunction { get; init; }

    private Func<IDataRow, object> GetInstanceInitFunction(
        Type type,
        IReadOnlyDictionary<string, int> columns
    )
    {
        var rowParameter = Expression.Parameter(typeof(IDataRow), "row");
        var columnsDict = columns.ToDictionary(x => x.Key.ToLowerInvariant(), x => x.Value);

        var constructorInfo = type.GetConstructors().MinBy(x => x.GetParameters().Length);
        if (constructorInfo == null)
            throw new NoConstructorsFoundException(type, new DefaultConstructorFinder()); //TODO: implement constructor finder or use another exception

        var parameters = constructorInfo.GetParameters();
        var constructorParameters = GetConstructorParameters(rowParameter, parameters, columnsDict);

        var bindings = GetBindingParameters(
            rowParameter,
            type,
            parameters.Select(x => x.Name).ToArray(),
            columnsDict
        );
        return Expression
            .Lambda<Func<IDataRow, object>>(
                Expression.MemberInit(
                    Expression.New(
                        constructorInfo,
                        constructorParameters.Select(x => x.expression)
                    ),
                    bindings
                ),
                rowParameter
            )
            .Compile();
    }

    private static IEnumerable<(string name, Expression expression)> GetConstructorParameters(
        ParameterExpression rowParameter,
        ParameterInfo[] parameters,
        Dictionary<string, int> columns
    )
    {
        foreach (var parameter in parameters)
        {
            //Here we need to specify defaults for parameters, to match parameters of ctor
            var listElementType = parameter.ParameterType.GetListElementType();
            if (listElementType != null)
            {
                var matchedColumns = GetMatchedColumnsForList(parameter.Name, columns);
                foreach (var matchedColumn in matchedColumns)
                    columns.Remove(matchedColumn.Key); // kick out parameters from columns to be processed in binding expressions

                if (matchedColumns.Length > 0)
                    yield return new(
                        parameter.Name,
                        GetListPropertyExpression(
                            rowParameter,
                            matchedColumns,
                            listElementType,
                            parameter.ParameterType.IsArray
                        )
                    );
                else
                    yield return new(parameter.Name, Expression.Default(parameter.ParameterType));
            }
            else
            {
                var name = parameter.GetCustomAttribute<MapToAttribute>()?.PropertyName ?? parameter.Name;
                if (columns.Remove(name!.ToLowerInvariant(), out var matchedIndex)) // kick out parameters from columns to be processed in binding expressions
                    yield return new(
                        parameter.Name,
                        GetPropertyExpression(rowParameter, matchedIndex, parameter.ParameterType)
                    );
                else
                    yield return new(parameter.Name, Expression.Default(parameter.ParameterType));
            }
        }
    }

    private IEnumerable<MemberBinding> GetBindingParameters(
        ParameterExpression rowParameter,
        Type type,
        string[] exceptProperties,
        Dictionary<string, int> columns
    )
    {
        // TODO: Exclude complexProperties like reference to another instance! (2020.11.15, Armen Sirotenko)
        foreach (
            var propertyInfo in type.GetProperties()
                .Where(x => x.CanWrite && !exceptProperties.Contains(x.Name))
        )
        {
            //Here we don't need to specify defaults for properties, since they are already equal to default
            var listElementType = propertyInfo.PropertyType.GetListElementType();
            if (listElementType != null)
            {
                var matchedColumns = GetMatchedColumnsForList(propertyInfo.Name, columns);
                if (matchedColumns.Length > 0)
                    yield return Expression.Bind(
                        propertyInfo,
                        GetListPropertyExpression(
                            rowParameter,
                            matchedColumns,
                            listElementType,
                            propertyInfo.PropertyType.IsArray
                        )
                    );
            }
            else
            {
                var propertyName =
                    propertyInfo.GetCustomAttribute<MapToAttribute>()?.PropertyName
                    ?? propertyInfo.Name;
                if (columns.TryGetValue(propertyName.ToLowerInvariant(), out var matchedIndex))
                    yield return Expression.Bind(
                        propertyInfo,
                        GetPropertyExpression(rowParameter, matchedIndex, propertyInfo.PropertyType)
                    );
            }
        }
    }

    private static KeyValuePair<string, int>[] GetMatchedColumnsForList(
        string name,
        Dictionary<string, int> columns
    )
    {
        var regexp = new Regex($"^{name.ToLowerInvariant()}\\d*$", RegexOptions.Compiled);
        return columns.Where(c => regexp.IsMatch(c.Key)).ToArray();
    }

    private static Expression GetListPropertyExpression(
        ParameterExpression rowParameter,
        KeyValuePair<string, int>[] matchedColumns,
        Type listElementType,
        bool isArray
    )
    {
        var body = new List<Expression> { rowParameter };

        var genericListType = typeof(List<>).MakeGenericType(listElementType);
        var listExpression = Expression.Parameter(genericListType, "list");

        body.Add(Expression.Assign(listExpression, Expression.New(genericListType)));

        Expression columnsIndexesArrayParameter = Expression.Constant(
            matchedColumns.Select(x => x.Value).ToArray()
        );
        var exitLabel = Expression.Label(typeof(void), "exit");
        var index = Expression.Parameter(typeof(int), "i");
        var itemExpression = Expression.Parameter(typeof(object), "item");
        body.Add(
            Expression.Block(
                new[] { index },
                Expression.Assign(index, Expression.Constant(0)),
                Expression.Loop(
                    Expression.IfThenElse(
                        Expression.LessThan(index, Expression.Constant(matchedColumns.Length)),
                        Expression.Block(
                            new[] { itemExpression },
                            Expression.Assign(
                                itemExpression,
                                Expression.MakeIndex(
                                    rowParameter,
                                    RowIndexer,
                                    new[]
                                    {
                                        Expression.ArrayIndex(columnsIndexesArrayParameter, index)
                                    }
                                )
                            ),
                            Expression.IfThen(
                                Expression.And(
                                    Expression.NotEqual(
                                        itemExpression,
                                        Expression.Constant(null, typeof(object))
                                    ),
                                    Expression.NotEqual(
                                        itemExpression,
                                        Expression.Constant(DBNull.Value)
                                    )
                                ),
                                Expression.Call(
                                    listExpression,
                                    genericListType.GetMethod("Add"),
                                    Expression.Convert(
                                        Expression.Condition(
                                            Expression.TypeIs(itemExpression, typeof(IConvertible)),
                                            Expression.Call(
                                                ConvertValueMethod,
                                                itemExpression,
                                                Expression.Constant(listElementType)
                                            ),
                                            itemExpression
                                        ),
                                        listElementType
                                    )
                                )
                            ),
                            Expression.PostIncrementAssign(index)
                        ),
                        Expression.Break(exitLabel)
                    ),
                    exitLabel
                )
            )
        );

        var returnExpression = isArray
            ? (Expression)Expression.Call(listExpression, genericListType.GetMethod("ToArray"))
            : listExpression;
        body.Add(returnExpression);

        var lambdaExpression = Expression.Lambda(
            Expression.Block(listExpression.RepeatOnce(), body.ToArray()),
            rowParameter
        );
        return Expression.Invoke(lambdaExpression, rowParameter);
    }

    private static Expression GetPropertyExpression(
        ParameterExpression rowParameter,
        int matchedIndex,
        Type propertyType
    )
    {
        var body = new List<Expression> { rowParameter };

        var item = Expression.Parameter(typeof(object), "item");
        body.Add(
            Expression.Assign(
                item,
                Expression.MakeIndex(
                    rowParameter,
                    RowIndexer,
                    new[] { Expression.Constant(matchedIndex) }
                )
            )
        );

        body.Add(
            Expression.Condition(
                Expression.Or(
                    Expression.Or(
                        Expression.Equal(item, Expression.Constant(null, typeof(object))),
                        Expression.Equal(item, Expression.Constant(DBNull.Value))
                    ),
                    Expression.Equal(
                        Expression.TypeAs(item, typeof(string)),
                        Expression.Constant(string.Empty)
                    )
                ),
                Expression.Default(propertyType),
                Expression.Convert(
                    Expression.Condition(
                        Expression.TypeIs(item, typeof(IConvertible)),
                        Expression.Call(
                            ConvertValueMethod,
                            item,
                            Expression.Constant(propertyType)
                        ),
                        item
                    ),
                    propertyType
                )
            )
        );

        var lambdaExpression = Expression.Lambda(
            Expression.Block(item.RepeatOnce(), body.ToArray()),
            rowParameter
        );

        return Expression.Invoke(lambdaExpression, rowParameter);
    }

    public static readonly MethodInfo ConvertValueMethod = ReflectionHelper.GetStaticMethod(
        () => ConvertValue(null, null)
    );

    private static readonly Dictionary<Type, Func<object, Type, object>> Converters =
        new()
        {
            { typeof(Guid), (value, type) => Guid.TryParse(value.ToString(), out var result) ? result : value.ToString().AsGuid() },
            { typeof(double), (value, type) => double.TryParse(value.ToString(), out var result) ? result : DefaultConversion(value, type) },
            { typeof(int), (value, type) => int.TryParse(value.ToString(), out var result) ? result : DefaultConversion(value, type) },
            { typeof(float), (value, type) => float.TryParse(value.ToString(), out var result) ? result : DefaultConversion(value, type) },
            { typeof(decimal), (value, type) => decimal.TryParse(value.ToString(), out var result) ? result : DefaultConversion(value, type) },
            { typeof(long), (value, type) => long.TryParse(value.ToString(), out var result) ? result : DefaultConversion(value, type) },
            { typeof(short), (value, type) => short.TryParse(value.ToString(), out var result) ? result : DefaultConversion(value, type) },
            { typeof(byte), (value, type) => byte.TryParse(value.ToString(), out var result) ? result : DefaultConversion(value, type) },
            { typeof(bool), (value, type) => bool.TryParse(value.ToString(), out var result) ? result : DefaultConversion(value, type) },
            { typeof(DateTime), (value, type) => DateTime.TryParse(value.ToString(), out var result) ? result : DefaultConversion(value, type) },
            { typeof(TimeSpan), (value, type) => TimeSpan.TryParse(value.ToString(), out var result) ? result : DefaultConversion(value, type) }
        };

    // ReSharper disable once UnusedMethodReturnValue.Local
    private static object ConvertValue(object value, Type type)
    {
        type =
            type.IsGenericType
            && typeof(Nullable<>).IsAssignableFrom(type.GetGenericTypeDefinition())
                ? type.GetGenericArguments()[0]
                : type;

        var valueStr = value.ToString();

        if (type == typeof(DateTime))
            return double.TryParse(valueStr, out var result)
                ? DateTime.FromOADate(result)
                : Convert.ChangeType(value, type, CultureInfo.InvariantCulture);

        if (type.IsEnum)
            return Enum.Parse(type, valueStr);

        //TODO return null if relationship
        return Converters.TryGetValue(type, out var converter)
            ? converter(value, type)
            : DefaultConversion(value, type);
    }

    private static object DefaultConversion(object value, Type type)
    => Convert.ChangeType(value, type, CultureInfo.InvariantCulture);


    public const string ParameterExceptionMessage =
        "Constructor for type {0} can not find parameters {1}";

    private static readonly PropertyInfo RowIndexer = typeof(IDataRow)
        .GetProperties()
        .First(x =>
            x.Name == "Item"
            && x.GetGetMethod()?.GetParameters().First().ParameterType == typeof(int)
        );
}
