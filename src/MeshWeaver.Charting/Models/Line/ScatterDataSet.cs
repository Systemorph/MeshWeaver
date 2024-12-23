using MeshWeaver.Charting.Enums;

namespace MeshWeaver.Charting.Models.Line
{
    public record ScatterDataSet(IReadOnlyCollection<object> Data, string Label = null)
        : LineDataSetBase<ScatterDataSet>(Data, Label)
    {
        internal override ChartType ChartType => ChartType.Scatter;
        public ScatterDataSet(IEnumerable<double> x, IEnumerable<int> y, string label = null) : this(x, y.Select(v => (double)v), label){}
        public ScatterDataSet(IEnumerable<int> x, IEnumerable<double> y, string label = null) : this(x.Select(v => (double)v), y, label){}
        public ScatterDataSet(IEnumerable<int> x, IEnumerable<int> y, string label = null) : this(x.Select(v => (double)v), y.Select(v => (double)v), label){}

        public ScatterDataSet(IEnumerable<double> x, IEnumerable<double> y, string label = null) : this(ConvertToData(x,y), label) { }

        private static IReadOnlyCollection<object> ConvertToData(IEnumerable<double> x, IEnumerable<double> y)
        {
            var xList = x.ToList();
            var yList = y.ToList();
            if (xList.Count != yList.Count)
                throw new InvalidOperationException();

            return xList
                .Zip(yList, (a, v) => new PointData { X = a, Y = v })
                .Cast<object>()
                .ToArray();

        }

        public ScatterDataSet(IEnumerable<(int x, int y)> points, string label = null) : this(points.Select(p => ((double)p.x, (double)p.y)), label){}
        public ScatterDataSet(IEnumerable<(int x, double y)> points, string label = null) : this(points.Select(p => ((double)p.x, p.y)), label){}
        public ScatterDataSet(IEnumerable<(double x, int y)> points, string label = null) : this(points.Select(p => (p.x, (double)p.y)), label){}

        public ScatterDataSet(IEnumerable<(double x, double y)> points, string label = null) : this(points.Select(p => new PointData { X = p.x, Y = p.y }).Cast<object>().ToArray(), label) { }

    }
}
