using System.Collections.Immutable;

namespace MeshWeaver.Layout;

/// <summary>
/// How a <see cref="UmlClass"/> is drawn — an OPEN vocabulary of string constants, never an enum
/// (policy <c>open-vocabulary-string-constants</c>): a renderer that meets a value it does not know
/// draws the class plainly.
/// </summary>
public static class UmlClassKind
{
    /// <summary>A class of the model the diagram is about (the default).</summary>
    public const string Local = "local";

    /// <summary>A class of ANOTHER package the model references — drawn subdued, labelled with its package.</summary>
    public const string Foreign = "foreign";

    /// <summary>A class the diagram is centred on or that its package features — drawn in the accent colour.</summary>
    public const string Featured = "featured";
}

/// <summary>What a <see cref="UmlRelation"/> means — an OPEN vocabulary; unknown kinds draw as an association.</summary>
public static class UmlRelationKind
{
    /// <summary>A reference from <see cref="UmlRelation.From"/> to <see cref="UmlRelation.To"/> (an open arrowhead at <c>To</c>).</summary>
    public const string Association = "association";

    /// <summary><see cref="UmlRelation.From"/> OWNS <see cref="UmlRelation.To"/> (a filled diamond at <c>From</c>).</summary>
    public const string Composition = "composition";

    /// <summary><see cref="UmlRelation.From"/> IS A <see cref="UmlRelation.To"/> (a hollow triangle at <c>To</c>).</summary>
    public const string Generalization = "generalization";
}

/// <summary>One attribute of a <see cref="UmlClass"/>: <c>name : Type [multiplicity]</c>.</summary>
/// <param name="Name">The attribute's name, as the content writes it.</param>
/// <param name="Type">Its type, short (<c>string</c>, <c>decimal</c>, <c>LineOfBusiness</c>).</param>
/// <param name="Multiplicity">UML multiplicity (<c>0..1</c>, <c>1</c>, <c>*</c>); null draws none.</param>
public record UmlAttribute(string Name, string Type, string? Multiplicity = null)
{
    /// <summary>Whether the attribute references another node (a <see cref="UmlRelation"/> usually draws it too).</summary>
    public bool IsReference { get; init; }

    /// <summary>The attribute's description (its XML doc) — the tooltip. Optional.</summary>
    public string? Description { get; init; }

    /// <summary>The attribute's compartment line: <c>name : Type [multiplicity]</c>.</summary>
    public string Line => string.IsNullOrEmpty(Multiplicity) ? $"{Name} : {Type}" : $"{Name} : {Type} [{Multiplicity}]";
}

/// <summary>One class box of a <see cref="UmlClassDiagramControl"/>.</summary>
/// <param name="Id">The class's identifier, unique within the diagram (a NodeType path).</param>
/// <param name="Name">The name in the box's header (already display-ready).</param>
public record UmlClass(string Id, string Name)
{
    /// <summary>A stereotype line above the name (<c>«dimension»</c> is written as <c>dimension</c>). Optional.</summary>
    public string? Stereotype { get; init; }

    /// <summary>The package the class belongs to — shown on a <see cref="UmlClassKind.Foreign"/> class. Optional.</summary>
    public string? Package { get; init; }

    /// <summary>Where a click on the box navigates; null renders it unlinked.</summary>
    public string? Href { get; init; }

    /// <summary>A <see cref="UmlClassKind"/> constant; defaults to <see cref="UmlClassKind.Local"/>.</summary>
    public string? Kind { get; init; }

    /// <summary>The class's description — the tooltip. Optional.</summary>
    public string? Description { get; init; }

    /// <summary>The attribute compartment, in order.</summary>
    public IReadOnlyList<UmlAttribute>? Attributes { get; init; }

    /// <summary>Whether the class is of <paramref name="kind"/> (case-insensitive; no kind is <see cref="UmlClassKind.Local"/>).</summary>
    /// <param name="kind">A <see cref="UmlClassKind"/> constant.</param>
    /// <returns>True when <see cref="Kind"/> is <paramref name="kind"/>.</returns>
    public bool IsKind(string kind) => string.Equals(
        string.IsNullOrWhiteSpace(Kind) ? UmlClassKind.Local : Kind, kind, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One relation between two <see cref="UmlClass"/>es.</summary>
/// <param name="From">The <see cref="UmlClass.Id"/> the relation starts at (the referencing class, the whole, the subclass).</param>
/// <param name="To">The <see cref="UmlClass.Id"/> it ends at (the referenced class, the part, the base class).</param>
/// <param name="Kind">A <see cref="UmlRelationKind"/> constant.</param>
public record UmlRelation(string From, string To, string Kind = UmlRelationKind.Association)
{
    /// <summary>The role name at <see cref="To"/> — the referencing property (<c>lineOfBusiness</c>). Optional.</summary>
    public string? Role { get; init; }

    /// <summary>The multiplicity at <see cref="From"/>. Optional.</summary>
    public string? FromMultiplicity { get; init; }

    /// <summary>The multiplicity at <see cref="To"/> (<c>0..1</c>, <c>1</c>, <c>*</c>). Optional.</summary>
    public string? ToMultiplicity { get; init; }

    /// <summary>Whether the relation is of <paramref name="kind"/> (case-insensitive; unknown is an association).</summary>
    /// <param name="kind">A <see cref="UmlRelationKind"/> constant.</param>
    /// <returns>True when <see cref="Kind"/> is <paramref name="kind"/>.</returns>
    public bool IsKind(string kind)
    {
        var own = Kind?.ToLowerInvariant() switch
        {
            UmlRelationKind.Composition => UmlRelationKind.Composition,
            UmlRelationKind.Generalization => UmlRelationKind.Generalization,
            _ => UmlRelationKind.Association,
        };
        return string.Equals(own, kind, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A point of the laid-out diagram, in diagram units (CSS pixels at zoom 1).</summary>
/// <param name="X">Horizontal position.</param>
/// <param name="Y">Vertical position.</param>
public record UmlPoint(double X, double Y);

/// <summary>A class placed on the canvas.</summary>
/// <param name="Class">The class.</param>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Box width.</param>
/// <param name="Height">Box height.</param>
/// <param name="Layer">The layer (row) the class was placed in, 0 at the top.</param>
/// <param name="Attributes">The attributes drawn — at most the diagram's cap.</param>
/// <param name="HiddenAttributes">How many attributes the cap left out (drawn as "+ n more").</param>
public record UmlBox(UmlClass Class, double X, double Y, double Width, double Height, int Layer,
    ImmutableList<UmlAttribute> Attributes, int HiddenAttributes);

/// <summary>A relation routed between two boxes: an orthogonal polyline from the <c>From</c> box to the <c>To</c> box.</summary>
/// <param name="Relation">The relation.</param>
/// <param name="Points">The polyline, starting on the <c>From</c> box's border and ending on the <c>To</c> box's.</param>
/// <param name="LabelAt">Where the role name sits.</param>
public record UmlRoute(UmlRelation Relation, ImmutableList<UmlPoint> Points, UmlPoint LabelAt);

/// <summary>The laid-out diagram: every class placed exactly once, every drawable relation routed.</summary>
/// <param name="Boxes">The placed classes.</param>
/// <param name="Routes">The routed relations.</param>
/// <param name="Width">The canvas width.</param>
/// <param name="Height">The canvas height.</param>
public record UmlDiagramLayout(ImmutableList<UmlBox> Boxes, ImmutableList<UmlRoute> Routes, double Width, double Height);

/// <summary>
/// A UML class diagram — class boxes with a header (stereotype, name, package for a foreign class) and an
/// attribute compartment (<c>name : Type [multiplicity]</c>), joined by associations (role name and
/// multiplicities), compositions and generalizations; every box optionally a link.
///
/// <para>The geometry is the pure <see cref="Layout(IEnumerable{UmlClass}, IEnumerable{UmlRelation}, int)"/>,
/// a layered (Sugiyama-style) layout pinned by unit tests: a referenced, owned or base class sits BELOW
/// the classes that reference, own or specialize it; cycles are broken deterministically; crossings are
/// reduced by barycentre sweeps; edges are routed orthogonally. A renderer only draws what it is given,
/// so every view of the control lays out the same model the same way.</para>
///
/// <para>Composable like any other leaf control. The rows may be given directly or bound to a data
/// section; <see cref="AnalysisRows.Resolve{T}"/> resolves either.</para>
/// </summary>
/// <param name="Classes">The classes, or a data-binding reference resolving to them.</param>
/// <param name="Relations">The relations, or a data-binding reference resolving to them.</param>
public record UmlClassDiagramControl(object Classes, object Relations)
    : UiControl<UmlClassDiagramControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>The default cap on attributes drawn per class.</summary>
    public const int DefaultMaxAttributes = 6;

    /// <summary>How many attributes a box draws before it says "+ n more". Null is <see cref="DefaultMaxAttributes"/>.</summary>
    public int? MaxAttributes { get; init; }

    /// <summary>Returns a copy drawing at most <paramref name="max"/> attributes per class.</summary>
    /// <param name="max">The cap.</param>
    /// <returns>The copy.</returns>
    public UmlClassDiagramControl WithMaxAttributes(int max) => this with { MaxAttributes = max };

    // Geometry, in diagram units. A renderer may scale the whole canvas; it never re-lays it out.
    internal const double CharWidth = 7.2;
    internal const double LineHeight = 18;
    internal const double HeaderHeight = 30;
    internal const double StereotypeHeight = 14;
    internal const double Padding = 10;
    internal const double MinWidth = 140;
    internal const double MaxWidth = 300;
    internal const double ColumnGap = 48;
    internal const double LayerGap = 72;

    /// <summary>
    /// Places the classes and routes the relations. Pure — the whole diagram geometry, kept out of the views.
    /// <list type="bullet">
    ///   <item>A repeated <see cref="UmlClass.Id"/> keeps its FIRST occurrence.</item>
    ///   <item>A relation naming an unknown class, or a class to itself, is not drawn.</item>
    ///   <item>Layers: the class a relation points at — the referenced class, the part, the base class —
    ///     sits below the class it starts from (for a generalization, the base sits ABOVE: UML draws the
    ///     triangle up). Cycles are broken by reversing back edges of a depth-first walk in input order.</item>
    ///   <item>Within a layer, classes are ordered by the barycentre of their neighbours, four sweeps,
    ///     ties kept in input order — deterministic for a given input.</item>
    ///   <item>No two boxes overlap; the canvas encloses every box and route.</item>
    /// </list>
    /// Returns <c>null</c> when there is no class to draw.
    /// </summary>
    /// <param name="classes">The classes.</param>
    /// <param name="relations">The relations.</param>
    /// <param name="maxAttributes">The cap on attributes drawn per class.</param>
    /// <returns>The layout, or <c>null</c>.</returns>
    public static UmlDiagramLayout? Layout(
        IEnumerable<UmlClass>? classes, IEnumerable<UmlRelation>? relations, int maxAttributes = DefaultMaxAttributes)
    {
        var unique = new List<UmlClass>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in (classes ?? []).Where(c => c is not null))
        {
            var cls = c.Id is null ? c with { Id = string.Empty } : c;
            if (index.TryAdd(cls.Id, unique.Count))
                unique.Add(cls);
        }
        if (unique.Count == 0)
            return null;

        var drawn = (relations ?? [])
            .Where(r => r is not null && r.From is not null && r.To is not null
                        && index.ContainsKey(r.From) && index.ContainsKey(r.To) && r.From != r.To)
            .ToList();

        // Layering direction: upper → lower.
        (int Upper, int Lower) Ends(UmlRelation r) => r.IsKind(UmlRelationKind.Generalization)
            ? (index[r.To], index[r.From])
            : (index[r.From], index[r.To]);

        var n = unique.Count;
        var down = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        foreach (var r in drawn)
        {
            var (u, l) = Ends(r);
            if (!down[u].Contains(l))
                down[u].Add(l);
        }

        // Break cycles: a depth-first walk in input order; an edge into a node on the stack is reversed.
        var state = new int[n]; // 0 unvisited, 1 on stack, 2 done
        var dag = Enumerable.Range(0, n).Select(_ => new HashSet<int>()).ToArray();
        for (var root = 0; root < n; root++)
        {
            if (state[root] != 0) continue;
            var stack = new Stack<(int Node, int Next)>();
            stack.Push((root, 0));
            state[root] = 1;
            while (stack.Count > 0)
            {
                var (v, next) = stack.Pop();
                if (next < down[v].Count)
                {
                    stack.Push((v, next + 1));
                    var w = down[v][next];
                    if (state[w] == 1) dag[w].Add(v);           // back edge: reversed
                    else
                    {
                        dag[v].Add(w);
                        if (state[w] == 0) { state[w] = 1; stack.Push((w, 0)); }
                    }
                }
                else state[v] = 2;
            }
        }

        // Longest-path layering over the acyclic graph, in topological order.
        var indegree = new int[n];
        foreach (var targets in dag) foreach (var w in targets) indegree[w]++;
        var layer = new int[n];
        var queue = new Queue<int>(Enumerable.Range(0, n).Where(v => indegree[v] == 0));
        while (queue.Count > 0)
        {
            var v = queue.Dequeue();
            foreach (var w in dag[v].OrderBy(x => x))
            {
                layer[w] = Math.Max(layer[w], layer[v] + 1);
                if (--indegree[w] == 0) queue.Enqueue(w);
            }
        }

        var layerCount = layer.Max() + 1;
        var rows = Enumerable.Range(0, layerCount)
            .Select(l => Enumerable.Range(0, n).Where(v => layer[v] == l).ToList())
            .ToList();

        // Neighbours across layers, for the barycentre.
        var neighbours = Enumerable.Range(0, n).Select(_ => new HashSet<int>()).ToArray();
        for (var v = 0; v < n; v++)
            foreach (var w in dag[v]) { neighbours[v].Add(w); neighbours[w].Add(v); }

        for (var sweep = 0; sweep < 4; sweep++)
        {
            var downward = sweep % 2 == 0;
            var order = downward ? Enumerable.Range(1, layerCount - 1) : Enumerable.Range(0, layerCount - 1).Reverse();
            foreach (var l in order)
            {
                var reference = rows[downward ? l - 1 : l + 1];
                var position = reference.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => (double)x.i);
                var current = rows[l];
                var keyed = current.Select((v, i) =>
                {
                    var adjacent = neighbours[v].Where(position.ContainsKey).Select(w => position[w]).ToList();
                    return (v, key: adjacent.Count == 0 ? i : adjacent.Average(), i);
                });
                rows[l] = keyed.OrderBy(x => x.key).ThenBy(x => x.i).Select(x => x.v).ToList();
            }
        }

        // Box sizes.
        var cap = Math.Max(0, maxAttributes);
        var shown = unique.Select(c => (c.Attributes ?? []).Where(a => a is not null).Take(cap).ToImmutableList()).ToArray();
        var hidden = unique.Select((c, i) => Math.Max(0, (c.Attributes?.Count(a => a is not null) ?? 0) - shown[i].Count)).ToArray();
        var widths = new double[n];
        var heights = new double[n];
        for (var v = 0; v < n; v++)
        {
            var c = unique[v];
            var texts = new List<string> { c.Name ?? string.Empty };
            if (!string.IsNullOrWhiteSpace(c.Stereotype)) texts.Add($"«{c.Stereotype}»");
            if (c.IsKind(UmlClassKind.Foreign) && !string.IsNullOrWhiteSpace(c.Package)) texts.Add(c.Package!);
            texts.AddRange(shown[v].Select(a => a.Line));
            widths[v] = Math.Clamp(texts.Max(t => t.Length) * CharWidth + 2 * Padding, MinWidth, MaxWidth);
            var lines = shown[v].Count + (hidden[v] > 0 ? 1 : 0);
            heights[v] = HeaderHeight
                         + (string.IsNullOrWhiteSpace(c.Stereotype) ? 0 : StereotypeHeight)
                         + (c.IsKind(UmlClassKind.Foreign) && !string.IsNullOrWhiteSpace(c.Package) ? StereotypeHeight : 0)
                         + (lines == 0 ? Padding : lines * LineHeight + Padding);
        }

        // Coordinates: rows stacked top-down; each row centred on the widest.
        var rowWidths = rows.Select(r => r.Sum(v => widths[v]) + ColumnGap * Math.Max(0, r.Count - 1)).ToList();
        var canvasWidth = rowWidths.Max() + 2 * ColumnGap;
        var x = new double[n];
        var y = new double[n];
        var top = LayerGap / 2;
        for (var l = 0; l < layerCount; l++)
        {
            var left = (canvasWidth - rowWidths[l]) / 2;
            foreach (var v in rows[l])
            {
                x[v] = left;
                y[v] = top;
                left += widths[v] + ColumnGap;
            }
            top += rows[l].Max(v => heights[v]) + LayerGap;
        }
        var canvasHeight = top - LayerGap / 2;

        // Routes: orthogonal, leaving the upper box at the bottom and entering the lower box at the top,
        // each box's ports spread evenly so parallel edges do not coincide.
        var outgoing = new Dictionary<int, List<UmlRelation>>();
        var incoming = new Dictionary<int, List<UmlRelation>>();
        foreach (var r in drawn)
        {
            var (u, l) = Oriented(r);
            (outgoing.TryGetValue(u, out var o) ? o : outgoing[u] = []).Add(r);
            (incoming.TryGetValue(l, out var i) ? i : incoming[l] = []).Add(r);
        }

        // Ports in the order of the box at the other end, left to right — so edges leaving (or entering)
        // one box fan out without crossing each other.
        double CentreOf(int v) => x[v] + widths[v] / 2;
        foreach (var (u, list) in outgoing)
            list.Sort((a, b) => CentreOf(Oriented(a).Lower).CompareTo(CentreOf(Oriented(b).Lower)));
        foreach (var (l, list) in incoming)
            list.Sort((a, b) => CentreOf(Oriented(a).Upper).CompareTo(CentreOf(Oriented(b).Upper)));

        (int Upper, int Lower) Oriented(UmlRelation r)
        {
            var (a, b) = Ends(r);
            return layer[a] <= layer[b] ? (a, b) : (b, a);
        }

        double Port(int box, List<UmlRelation> list, UmlRelation r) =>
            x[box] + widths[box] * (list.IndexOf(r) + 1) / (list.Count + 1);

        var routes = ImmutableList.CreateBuilder<UmlRoute>();
        foreach (var r in drawn)
        {
            var (u, l) = Oriented(r);
            var start = new UmlPoint(Port(u, outgoing[u], r), y[u] + heights[u]);
            var end = new UmlPoint(Port(l, incoming[l], r), y[l]);
            UmlPoint[] points;
            if (layer[u] == layer[l])
            {
                // Same row (only after a cycle reversal between equal layers): leave and enter at the bottom.
                var below = Math.Max(y[u] + heights[u], y[l] + heights[l]) + LayerGap / 3;
                end = new UmlPoint(end.X, y[l] + heights[l]);
                points = [start, new UmlPoint(start.X, below), new UmlPoint(end.X, below), end];
            }
            else
            {
                // Each edge leaving a box turns at its own height, so the horizontal legs of
                // parallel edges never run on top of each other.
                var list = outgoing[u];
                var midY = y[u] + heights[u] + LayerGap * (list.IndexOf(r) + 1) / (list.Count + 1);
                points = [start, new UmlPoint(start.X, midY), new UmlPoint(end.X, midY), end];
            }
            // The polyline runs From → To, whichever box is on top.
            var fromIsUpper = index[r.From] == u;
            var ordered = (fromIsUpper ? points : points.Reverse()).ToImmutableList();
            var label = new UmlPoint((points[1].X + points[2].X) / 2, points[1].Y);
            routes.Add(new UmlRoute(r, ordered, label));
        }

        var boxes = Enumerable.Range(0, n)
            .Select(v => new UmlBox(unique[v], x[v], y[v], widths[v], heights[v], layer[v], shown[v], hidden[v]))
            .ToImmutableList();
        return new UmlDiagramLayout(boxes, routes.ToImmutable(), canvasWidth, Math.Max(canvasHeight, LayerGap));
    }

    /// <summary>
    /// Whether <paramref name="cls"/> is drawn as a link — returns the navigable href
    /// (see <see cref="TowerControl.NavigableHref"/>), or null for an unlinked box.
    /// </summary>
    /// <param name="cls">The class.</param>
    /// <returns>The href to navigate to, or <c>null</c>.</returns>
    public static string? LinkOf(UmlClass cls) => TowerControl.NavigableHref(cls.Href);
}
