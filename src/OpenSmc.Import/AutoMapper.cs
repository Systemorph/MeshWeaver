using System.Collections.Immutable;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using Autofac.Core.Activators.Reflection;
using OpenSmc.Collections;
using OpenSmc.DataStructures;
using OpenSmc.Domain;
using OpenSmc.Reflection;
using OpenSmc.ShortGuid;

// ReSharper disable AssignNullToNotNullAttribute

namespace OpenSmc.Import
{
    public record AutoMapConfiguration
    {
        internal Func<IDataSet, IDataTable, IDataRow, IEnumerable<object>> RowMapping { get; init; }
    }

    public static class AutoMapper
    {
        public static ImmutableDictionary<string, TableMapping> Create(IEnumerable<Type> types)
        {
            return types
                .Select(type => new KeyValuePair<string, TableMapping>(
                    type.Name,
                    new TableMapping(type.Name, (_, table) => MapTableToType(type, table))
                ))
                .ToImmutableDictionary();
        }

        private static IEnumerable<object> MapTableToType(Type type, IDataTable table)
        {
            var constructor = GetInstanceInitFunction(
                type,
                table
                    .Columns.Select((c, i) => new KeyValuePair<string, int>(c.ColumnName, i))
                    .ToDictionary()
            );
            return table.Rows.Select(constructor);
        }

        public const string ParameterExceptionMessage =
            "Constructor for type {0} can not find parameters {1}";

        private static readonly PropertyInfo RowIndexer = typeof(IDataRow)
            .GetProperties()
            .First(x =>
                x.Name == "Item"
                && x.GetGetMethod()?.GetParameters().First().ParameterType == typeof(int)
            );

        private static Func<IDataRow, object> GetInstanceInitFunction(
            Type type,
            IReadOnlyDictionary<string, int> columns
        )
        {
            var rowParameter = Expression.Parameter(typeof(IDataRow), "row");
            var columnsDict = columns.ToDictionary(x => x.Key, x => x.Value);

            var constructorInfo = type.GetConstructors().MinBy(x => x.GetParameters().Length);
            if (constructorInfo == null)
                throw new NoConstructorsFoundException(type, new DefaultConstructorFinder()); //TODO: implement constructor finder or use another exception

            var parameters = constructorInfo.GetParameters();
            var constructorParameters = GetConstructorParameters(
                rowParameter,
                parameters,
                columnsDict
            );

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
                        yield return new(
                            parameter.Name,
                            Expression.Default(parameter.ParameterType)
                        );
                }
                else
                {
                    var name =
                        parameter.GetCustomAttribute<MapToAttribute>()?.PropertyName
                        ?? parameter.Name;
                    if (columns.Remove(name, out var matchedIndex)) // kick out parameters from columns to be processed in binding expressions
                        yield return new(
                            parameter.Name,
                            GetPropertyExpression(
                                rowParameter,
                                matchedIndex,
                                parameter.ParameterType
                            )
                        );
                    else
                        yield return new(
                            parameter.Name,
                            Expression.Default(parameter.ParameterType)
                        );
                }
            }
        }

        private static IEnumerable<MemberBinding> GetBindingParameters(
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
                    if (columns.TryGetValue(propertyName, out var matchedIndex))
                        yield return Expression.Bind(
                            propertyInfo,
                            GetPropertyExpression(
                                rowParameter,
                                matchedIndex,
                                propertyInfo.PropertyType
                            )
                        );
                }
            }
        }

        private static KeyValuePair<string, int>[] GetMatchedColumnsForList(
            string name,
            Dictionary<string, int> columns
        )
        {
            var regexp = new Regex($"^{name}\\d*$", RegexOptions.Compiled);
            return columns.Where(c => regexp.IsMatch(c.Key)).ToArray();
        }

        private static Expression GetListPropertyExpression(
            ParameterExpression rowParameter,
            KeyValuePair<string, int>[] matchedColumns,
            Type listElementType,
            bool isArray
        )
        {
            List<Expression> body = new List<Expression> { rowParameter };

            Type genericListType = typeof(List<>).MakeGenericType(listElementType);
            ParameterExpression listExpression = Expression.Parameter(genericListType, "list");

            body.Add(Expression.Assign(listExpression, Expression.New(genericListType)));

            Expression columnsIndexesArrayParameter = Expression.Constant(
                matchedColumns.Select(x => x.Value).ToArray()
            );
            LabelTarget exitLabel = Expression.Label(typeof(void), "exit");
            ParameterExpression index = Expression.Parameter(typeof(int), "i");
            ParameterExpression itemExpression = Expression.Parameter(typeof(object), "item");
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
                                            Expression.ArrayIndex(
                                                columnsIndexesArrayParameter,
                                                index
                                            )
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
                                                Expression.TypeIs(
                                                    itemExpression,
                                                    typeof(IConvertible)
                                                ),
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
            List<Expression> body = new List<Expression> { rowParameter };

            ParameterExpression item = Expression.Parameter(typeof(object), "item");
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

            if (type == typeof(Guid))
                return Guid.TryParse(valueStr, out var result) ? result : valueStr.AsGuid();
            //TODO return null if relationship
            return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }
    }
}
