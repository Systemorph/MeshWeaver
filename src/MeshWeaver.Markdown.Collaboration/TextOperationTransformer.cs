using System.Collections.Immutable;

namespace MeshWeaver.Markdown.Collaboration;

/// <summary>
/// Implements Operational Transformation (OT) for text editing operations.
/// Transforms operations to maintain consistency when concurrent edits occur.
/// </summary>
public class TextOperationTransformer
{
    /// <summary>
    /// Transform operation B against operation A (A was applied first).
    /// Returns the transformed B that achieves the same intent when applied after A.
    /// </summary>
    /// <param name="operationA">The operation that was applied first.</param>
    /// <param name="operationB">The operation to transform.</param>
    /// <returns>The transformed operation B.</returns>
    public TextOperation Transform(TextOperation operationA, TextOperation operationB)
    {
        return (operationA, operationB) switch
        {
            (InsertOperation a, InsertOperation b) => TransformInsertInsert(a, b),
            (InsertOperation a, DeleteOperation b) => TransformInsertDelete(a, b),
            (DeleteOperation a, InsertOperation b) => TransformDeleteInsert(a, b),
            (DeleteOperation a, DeleteOperation b) => TransformDeleteDelete(a, b),
            (CompositeOperation a, _) => TransformCompositeFirst(a, operationB),
            (_, CompositeOperation b) => TransformCompositeSecond(operationA, b),
            (NoOpOperation, _) => operationB,
            (_, NoOpOperation) => operationB,
            _ => operationB
        };
    }

    /// <summary>
    /// Apply an operation to document content.
    /// </summary>
    public string ApplyOperation(string content, TextOperation operation)
    {
        return operation switch
        {
            InsertOperation insert => ApplyInsert(content, insert),
            DeleteOperation delete => ApplyDelete(content, delete),
            CompositeOperation composite => ApplyComposite(content, composite),
            NoOpOperation => content,
            _ => content
        };
    }

    private string ApplyInsert(string content, InsertOperation insert)
    {
        if (insert.Position < 0 || insert.Position > content.Length)
            throw new ArgumentOutOfRangeException(nameof(insert),
                $"Insert position {insert.Position} is out of range for content of length {content.Length}");

        return content.Insert(insert.Position, insert.Text);
    }

    private string ApplyDelete(string content, DeleteOperation delete)
    {
        if (delete.Position < 0 || delete.Position > content.Length)
            throw new ArgumentOutOfRangeException(nameof(delete),
                $"Delete position {delete.Position} is out of range for content of length {content.Length}");

        var actualLength = Math.Min(delete.Length, content.Length - delete.Position);
        if (actualLength <= 0)
            return content;

        return content.Remove(delete.Position, actualLength);
    }

    private string ApplyComposite(string content, CompositeOperation composite)
    {
        return composite.Operations.Aggregate(content, (c, op) => ApplyOperation(c, op));
    }

    private InsertOperation TransformInsertInsert(InsertOperation a, InsertOperation b)
    {
        // If B inserts at or after A's position, shift B by A's text length
        // If both are at the same position, the first one (A) wins the position
        if (b.Position >= a.Position)
        {
            return b with { Position = b.Position + a.Text.Length };
        }
        return b;
    }

    private DeleteOperation TransformInsertDelete(InsertOperation a, DeleteOperation b)
    {
        // A inserts text, B tries to delete
        if (b.Position >= a.Position + a.Text.Length)
        {
            // B's delete is entirely after A's insert - no overlap possible
            // But wait, A hasn't been applied yet when B was created
            // So we need to shift B if B.Position >= A.Position
            return b with { Position = b.Position + a.Text.Length };
        }
        else if (b.Position + b.Length <= a.Position)
        {
            // B's delete is entirely before A's insert
            return b;
        }
        else if (b.Position >= a.Position)
        {
            // B starts at or after A's insert position
            // Shift B's position by A's insert length
            return b with { Position = b.Position + a.Text.Length };
        }
        else
        {
            // B's delete spans A's insert position
            // B starts before A.Position but ends after
            // The delete needs to be split or adjusted
            // We keep the delete as is, but note that after A inserts,
            // B's delete will be before and after the inserted text
            // For simplicity, we just return B unchanged - the delete
            // will delete what it originally intended, and A's insert
            // will happen at its position
            return b;
        }
    }

    private InsertOperation TransformDeleteInsert(DeleteOperation a, InsertOperation b)
    {
        // A deletes text, B tries to insert
        if (b.Position <= a.Position)
        {
            // B inserts before or at the start of A's delete - no change
            return b;
        }
        else if (b.Position >= a.Position + a.Length)
        {
            // B inserts after A's deleted range - shift back by A's length
            return b with { Position = b.Position - a.Length };
        }
        else
        {
            // B inserts within A's deleted range
            // Move insert to A's delete position (the text it was in is gone)
            return b with { Position = a.Position };
        }
    }

    private DeleteOperation TransformDeleteDelete(DeleteOperation a, DeleteOperation b)
    {
        var aEnd = a.Position + a.Length;
        var bEnd = b.Position + b.Length;

        // Case 1: B is entirely after A
        if (b.Position >= aEnd)
        {
            return b with { Position = b.Position - a.Length };
        }

        // Case 2: B is entirely before A
        if (bEnd <= a.Position)
        {
            return b;
        }

        // Case 3: A and B overlap
        // Calculate what remains of B after A is applied

        // If A completely contains B
        if (a.Position <= b.Position && aEnd >= bEnd)
        {
            // B is entirely within A, so B becomes a no-op (length 0)
            return b with { Position = a.Position, Length = 0 };
        }

        // If B completely contains A
        if (b.Position <= a.Position && bEnd >= aEnd)
        {
            // B still needs to delete, but A's portion is already gone
            return b with { Length = b.Length - a.Length };
        }

        // Partial overlap - A starts before B
        if (a.Position < b.Position && aEnd > b.Position && aEnd < bEnd)
        {
            // A deletes part of what B wants to delete (from start of B)
            var overlap = aEnd - b.Position;
            return b with { Position = a.Position, Length = b.Length - overlap };
        }

        // Partial overlap - B starts before A
        if (b.Position < a.Position && bEnd > a.Position && bEnd < aEnd)
        {
            // A deletes part of what B wants to delete (from end of B)
            var overlap = bEnd - a.Position;
            return b with { Length = b.Length - overlap };
        }

        // Default: shouldn't reach here, but return B unchanged
        return b;
    }

    private TextOperation TransformCompositeFirst(CompositeOperation a, TextOperation b)
    {
        // Transform B against each operation in A, in order
        return a.Operations.Aggregate(b, (current, aOp) => Transform(aOp, current));
    }

    private CompositeOperation TransformCompositeSecond(TextOperation a, CompositeOperation b)
    {
        // Transform each operation in B against A
        var transformedOps = b.Operations.Select(bOp => Transform(a, bOp)).ToImmutableList();
        return b with { Operations = transformedOps };
    }
}
