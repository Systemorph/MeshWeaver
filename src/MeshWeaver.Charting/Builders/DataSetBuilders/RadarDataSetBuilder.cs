using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Radar;

namespace MeshWeaver.Charting.Builders.DataSetBuilders;

public record RadarDataSetBuilder : ArrayDataSetWithTensionFillPointRadiusAndRotation<RadarDataSetBuilder, RadarDataSet>;