using System.Collections.Generic;
using System.Text;

namespace MeshWeaver.AI;

/// <summary>
/// Minimal unified-diff generator for small JSON-node snapshots. Used by the MCP
/// tool responses so callers see exactly which lines changed after a
/// <c>patch</c> / <c>update</c> / <c>create</c> / <c>delete</c> mutation.
///
/// The implementation is a naïve quadratic LCS — good enough for MeshNode JSON
/// (typically a few hundred lines pretty-printed). If we ever need to diff
/// massive payloads, swap the core for a Meyers-based algorithm behind the same
/// <see cref="UnifiedDiff"/> signature.
/// </summary>
internal static class DiffUtil
{
    /// <summary>
    /// Produce a unified diff between two text blobs. The output begins with
    /// standard <c>---</c> / <c>+++</c> headers and each line is prefixed with
    /// <c>-</c> (removed), <c>+</c> (added), or a space (unchanged context).
    /// A consumer can wrap the return value in a <c>```diff</c> markdown fence
    /// for syntax highlighting.
    /// </summary>
    public static string UnifiedDiff(string? before, string? after, string label)
    {
        var b = Split(before);
        var a = Split(after);
        var ops = ComputeOps(b, a);

        var sb = new StringBuilder();
        sb.Append("--- ").Append(label).Append(" (before)\n");
        sb.Append("+++ ").Append(label).Append(" (after)\n");
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case '-': sb.Append('-').Append(b[op.Index]).Append('\n'); break;
                case '+': sb.Append('+').Append(a[op.Index]).Append('\n'); break;
                default:  sb.Append(' ').Append(b[op.Index]).Append('\n'); break;
            }
        }
        return sb.ToString();
    }

    private static string[] Split(string? s) =>
        (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private readonly record struct Op(char Kind, int Index);

    private static List<Op> ComputeOps(string[] b, string[] a)
    {
        // Quadratic LCS table. Rows index `before` (b), columns index `after` (a).
        int m = b.Length, n = a.Length;
        var len = new int[m + 1, n + 1];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < n; j++)
                len[i + 1, j + 1] = b[i] == a[j]
                    ? len[i, j] + 1
                    : System.Math.Max(len[i + 1, j], len[i, j + 1]);

        // Backtrack to emit ops in reverse-chronological order, then reverse.
        var ops = new List<Op>(m + n);
        int x = m, y = n;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && b[x - 1] == a[y - 1])
            {
                ops.Add(new Op(' ', x - 1));
                x--; y--;
            }
            else if (y > 0 && (x == 0 || len[x, y - 1] >= len[x - 1, y]))
            {
                ops.Add(new Op('+', y - 1));
                y--;
            }
            else
            {
                ops.Add(new Op('-', x - 1));
                x--;
            }
        }
        ops.Reverse();
        return ops;
    }
}
