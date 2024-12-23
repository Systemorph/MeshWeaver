using MeshWeaver.Charting.Enums;

namespace MeshWeaver.Charting.Models.Line
{
    public record LineScatterDataSet(IReadOnlyCollection<object> Data) : LineDataSet(Data)
    {
        internal override ChartType ChartType => ChartType.Scatter;
        public LineScatterDataSet(IEnumerable<double> x, IEnumerable<int> y) : this(x, y.Select(v => (double)v)){}
        public LineScatterDataSet(IEnumerable<int> x, IEnumerable<double> y) : this(x.Select(v => (double)v), y){}
        public LineScatterDataSet(IEnumerable<int> x, IEnumerable<int> y) : this(x.Select(v => (double)v), y.Select(v => (double)v)){}

        public LineScatterDataSet(IEnumerable<double> x, IEnumerable<double> y) : this(ConvertToData(x,y)) { }

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

        public LineScatterDataSet(IEnumerable<(int x, int y)> points) : this(points.Select(p => ((double)p.x, (double)p.y))){}
        public LineScatterDataSet(IEnumerable<(int x, double y)> points) : this(points.Select(p => ((double)p.x, p.y))){}
        public LineScatterDataSet(IEnumerable<(double x, int y)> points) : this(points.Select(p => (p.x, (double)p.y))){}

        public LineScatterDataSet(IEnumerable<(double x, double y)> points) : this(points.Select(p => new PointData { X = p.x, Y = p.y }).Cast<object>().ToArray()) { }
    }
}
