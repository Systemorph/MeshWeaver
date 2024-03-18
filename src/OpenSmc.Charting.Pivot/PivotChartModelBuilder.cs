﻿using System.Text;
using OpenSmc.Charting.Enums;
using OpenSmc.Pivot.Grouping;
using OpenSmc.Pivot.Models;

namespace OpenSmc.Charting.Pivot
{
    public record PivotChartModelBuilder()
    {
        private readonly IList<Func<PivotChartModel, PivotChartModel>> postProcessors = new List<Func<PivotChartModel, PivotChartModel>> { x => x };
        private bool WithTotals { get; set; } = false;
        private PivotModel PivotModel { get; set; }

        // TODO V10: do not instantiate this model, only inside build method and return from build (2022/10/06, Ekaterina Mishina)
        private PivotChartModel PivotChartModel { get; set; } = new();

        public PivotChartModel BuildFromPivotModel(PivotModel pivotModel, ChartType defaultChartType, bool withTotals = false)
        {
            PivotModel = pivotModel;
            WithTotals = withTotals;
            MapPivotToChartModel(defaultChartType);
            PivotChartModel PostProcessor(PivotChartModel model) => postProcessors.Aggregate(model, (current, next) => next(current));
            PivotChartModel = PostProcessor(PivotChartModel);
            return PivotChartModel;
        }

        private void MapPivotToChartModel(ChartType defaultChartType)
        {
            PivotChartModel.RowGroupings.UnionWith(PivotModel.Rows.Select(x => x.RowGroup?.GrouperName).ToHashSet());

            //build column coordinate map
            //example: Coordinate (based on systemName) => { DisplayName, DimensionName }
            //example: G.E => { Germany, Country }
            BuildColumnCoordinateMapInner();

            //for FlatPivotModel
            //build row coordinate map
            //example: SystemName => { DisplayName, DimensionName }
            //example: B.EUR => { Euro, Currency }

            AddRows(defaultChartType);
            SetDefaultColumnLabels();
        }

        private void BuildColumnCoordinateMapInner()
        {
            var headerDimensions = new Dictionary<string, IList<Tuple<string, string, string>>>();
            var valueDimensions = new Dictionary<string, string>();

            var displayNameToDimensions = new List<Tuple<string, string, string>>();

            BuildColumnCoordinateMap(PivotModel.Columns, headerDimensions, valueDimensions, displayNameToDimensions);

            // TODO V10: simplify this, write directly to the model in the method above (2022/10/06, Ekaterina Mishina)
            // TODO V10: Why do we need a merger if vd is never used? Do we need valueDimension at all? (2022/10/21, Andrey Katz)
            var descriptors = from hd in headerDimensions
                              join vd in valueDimensions on hd.Key equals vd.Key
                              select new PivotElementDescriptor()
                              {
                                  SystemName = hd.Key,
                                  Coordinates = hd.Value.Select(y => (y.Item1, y.Item2, y.Item3)).ToList()
                              };
            PivotChartModel.ColumnDescriptors = descriptors.ToList();
        }

        private IList<Tuple<string, string, string>> BuildColumnCoordinateMap(IReadOnlyCollection<Column> pivotColumns,
                                                                                     IDictionary<string, IList<Tuple<string, string, string>>> headerDimensions,
                                                                                     IDictionary<string, string> valueDimensions,
                                                                                     IList<Tuple<string, string, string>> displayNameToDimensions)
        {
            for (var i = 0; i < pivotColumns.Count; i++)
            {
                //in case we reach last column which represents "value"
                //"value" can be many
                var deadEnd = true;
                var currentElement = pivotColumns.ElementAt(i);
                if (currentElement is ColumnGroup colGroup)
                {
                    deadEnd = false;
                    if (!WithTotals && colGroup.Coordinates.Last() == IPivotGrouper<object, ColumnGroup>.TotalGroup.SystemName)
                        continue; // we do not want to export aggregated values
                    var item = new Tuple<string, string, string>(colGroup.Coordinates.Last(), colGroup.DisplayName, colGroup.GrouperName);
                    displayNameToDimensions.Add(item);
                    PivotChartModel.ColumnGroupings.Add(colGroup.GrouperName);
                    displayNameToDimensions = BuildColumnCoordinateMap(colGroup.Children, headerDimensions, valueDimensions, displayNameToDimensions);
                    if (displayNameToDimensions.Any() && Equals(displayNameToDimensions.Last(), item))
                        displayNameToDimensions.Remove(item);
                }

                if (deadEnd)
                {
                    var coordinate = new StringBuilder().Append(currentElement.Coordinates.First());
                    for (var j = 0; j < currentElement.Coordinates.Count - 1; j++)
                        coordinate.Append($".{currentElement.Coordinates[j + 1]}");

                    var valueDisplayName = pivotColumns.ElementAt(i).DisplayName;
                    var item = new Tuple<string, string, string>(pivotColumns.ElementAt(i).Coordinates.Last(), pivotColumns.ElementAt(i).DisplayName, PivotChartConst.Column);
                    displayNameToDimensions.Add(item);
                    valueDimensions.Add(coordinate.ToString(), valueDisplayName);
                    headerDimensions.Add(coordinate.ToString(), displayNameToDimensions);
                    if (displayNameToDimensions.Any() && Equals(displayNameToDimensions.Last(), item))
                        displayNameToDimensions = displayNameToDimensions.SkipLast(1).ToList();
                }
            }

            return displayNameToDimensions;
        }

        private void AddRows(ChartType defaultChartType)
        {
            foreach (var pivotRow in PivotModel.Rows)
            {
                var rowGroup = pivotRow.RowGroup;
                var row = new PivotChartRow
                          {
                              DataSetType = defaultChartType,
                              Descriptor = new PivotElementDescriptor
                                           {
                                               SystemName = rowGroup.SystemName,
                                               Coordinates = rowGroup.Coordinates.Select((_, j) =>
                                                                                         {
                                                                                             var cRow = PivotChartModel.Rows.FirstOrDefault(r => r.Descriptor.SystemName == String.Join(".", rowGroup.Coordinates.Take(j + 1)));
                                                                                             return cRow != null
                                                                                                        ? cRow.Descriptor.Coordinates.Last()
                                                                                                        : (rowGroup.SystemName, rowGroup.DisplayName, rowGroup.GrouperName);
                                                                                         }).ToList()
                                           }
                          };
                row = row with
                      {
                          Descriptor = row.Descriptor with
                                       {
                                           DisplayName = String.Join(".", row.Descriptor.Coordinates
                                                                             .Select(c => c.DisplayName))
                                       }
                      };
                PivotChartModel.Rows.Add(row);
                AddDataToRow((IDictionary<string, object>)pivotRow.Data, row);
            }
        }

        private void AddDataToRow(IDictionary<string, object> data, PivotChartRow chartRow)
        {
            foreach (var columnDescriptor in PivotChartModel.ColumnDescriptors)
            {
                var coordinates = columnDescriptor.Coordinates;
                var value = GetValue(data, coordinates);
                chartRow.DataByColumns.Add((columnDescriptor.SystemName, value));
            }
        }

        private double? GetValue(IDictionary<string, object> data, IList<(string SystemName, string DisplayName, string GrouperName)> coordinates)
        {
            if (!coordinates.Any() || data is null)
                return null;


            if (data.TryGetValue(coordinates.First().SystemName, out var subData))
            {
                if (subData is IDictionary<string, object> subDataDict)
                    return GetValue(subDataDict, coordinates.Skip(1).ToList());
                if (subData is int intSubData) return (double?)intSubData;
                return (double?)subData;
            }

            return null;
        }

        private void SetDefaultColumnLabels()
        {
            var nbValueColumns = PivotChartModel.ColumnDescriptors.Select(col => col.Coordinates.Last()).Distinct().ToList().Count;
            PivotChartModel = PivotChartModel with
            {
                ColumnDescriptors = PivotChartModel.ColumnDescriptors
                                                                     .Select(desc => desc with
                                                                     {
                                                                         DisplayName = String.Join(".", (nbValueColumns == 1
                                                                                                                             ? desc.Coordinates.SkipLast(1)
                                                                                                                             : desc.Coordinates)
                                                                                                   .Select(c => c.DisplayName))
                                                                     })
                                                                     .ToList()
            };
        }

        public void AddPostProcessor(Func<PivotChartModel, PivotChartModel> newPostProcessor)
        {
            postProcessors.Add(newPostProcessor);
        }
    }
}