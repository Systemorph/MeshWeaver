using FluentAssertions;
using MeshWeaver.Markdown.Collaboration;
using Xunit;

namespace MeshWeaver.Markdown.Collaboration.Test;

public class TextOperationTransformerTests
{
    private readonly TextOperationTransformer _transformer = new();

    #region Insert vs Insert Tests

    [Fact]
    public void Transform_InsertInsert_SamePosition_SecondShiftsRight()
    {
        // Arrange: Both users insert at position 5
        var opA = new InsertOperation { Position = 5, Text = "AAA" };
        var opB = new InsertOperation { Position = 5, Text = "BBB" };

        // Act: Transform B against A (A was applied first)
        var transformed = _transformer.Transform(opA, opB);

        // Assert: B should shift right by A's length
        var result = (InsertOperation)transformed;
        result.Position.Should().Be(8); // 5 + 3
        result.Text.Should().Be("BBB");
    }

    [Fact]
    public void Transform_InsertInsert_BBeforeA_NoChange()
    {
        // Arrange: B inserts before A
        var opA = new InsertOperation { Position = 10, Text = "AAA" };
        var opB = new InsertOperation { Position = 5, Text = "BBB" };

        // Act
        var transformed = _transformer.Transform(opA, opB);

        // Assert: B should not change
        var result = (InsertOperation)transformed;
        result.Position.Should().Be(5);
        result.Text.Should().Be("BBB");
    }

    [Fact]
    public void Transform_InsertInsert_BAfterA_Shifts()
    {
        // Arrange: B inserts after A
        var opA = new InsertOperation { Position = 5, Text = "AAA" };
        var opB = new InsertOperation { Position = 10, Text = "BBB" };

        // Act
        var transformed = _transformer.Transform(opA, opB);

        // Assert: B should shift by A's length
        var result = (InsertOperation)transformed;
        result.Position.Should().Be(13); // 10 + 3
    }

    #endregion

    #region Insert vs Delete Tests

    [Fact]
    public void Transform_InsertDelete_DeleteAfterInsert_ShiftsRight()
    {
        // Arrange: A inserts, B deletes after the insert position
        var opA = new InsertOperation { Position = 5, Text = "XXX" };
        var opB = new DeleteOperation { Position = 10, Length = 3 };

        // Act
        var transformed = _transformer.Transform(opA, opB);

        // Assert: Delete position shifts right
        var result = (DeleteOperation)transformed;
        result.Position.Should().Be(13); // 10 + 3
        result.Length.Should().Be(3);
    }

    [Fact]
    public void Transform_InsertDelete_DeleteBeforeInsert_NoChange()
    {
        // Arrange: A inserts, B deletes before the insert position
        var opA = new InsertOperation { Position = 10, Text = "XXX" };
        var opB = new DeleteOperation { Position = 2, Length = 3 };

        // Act
        var transformed = _transformer.Transform(opA, opB);

        // Assert: Delete should not change
        var result = (DeleteOperation)transformed;
        result.Position.Should().Be(2);
        result.Length.Should().Be(3);
    }

    #endregion

    #region Delete vs Insert Tests

    [Fact]
    public void Transform_DeleteInsert_InsertBeforeDelete_NoChange()
    {
        // Arrange: A deletes, B inserts before the delete
        var opA = new DeleteOperation { Position = 10, Length = 5 };
        var opB = new InsertOperation { Position = 5, Text = "XXX" };

        // Act
        var transformed = _transformer.Transform(opA, opB);

        // Assert: Insert should not change
        var result = (InsertOperation)transformed;
        result.Position.Should().Be(5);
    }

    [Fact]
    public void Transform_DeleteInsert_InsertAfterDelete_ShiftsBack()
    {
        // Arrange: A deletes, B inserts after the deleted range
        var opA = new DeleteOperation { Position = 5, Length = 5 }; // Deletes 5-10
        var opB = new InsertOperation { Position = 15, Text = "XXX" };

        // Act
        var transformed = _transformer.Transform(opA, opB);

        // Assert: Insert shifts back by delete length
        var result = (InsertOperation)transformed;
        result.Position.Should().Be(10); // 15 - 5
    }

    [Fact]
    public void Transform_DeleteInsert_InsertWithinDeletedRange_MovesToDeletePosition()
    {
        // Arrange: A deletes range, B tries to insert within that range
        var opA = new DeleteOperation { Position = 5, Length = 10 }; // Deletes 5-15
        var opB = new InsertOperation { Position = 10, Text = "XXX" };

        // Act
        var transformed = _transformer.Transform(opA, opB);

        // Assert: Insert moves to delete position (the text it was in is gone)
        var result = (InsertOperation)transformed;
        result.Position.Should().Be(5);
    }

    #endregion

    #region Delete vs Delete Tests

    [Fact]
    public void Transform_DeleteDelete_NonOverlapping_BAfterA_ShiftsBack()
    {
        // Arrange: A deletes 0-5, B deletes 10-13
        var opA = new DeleteOperation { Position = 0, Length = 5 };
        var opB = new DeleteOperation { Position = 10, Length = 3 };

        // Act
        var transformed = _transformer.Transform(opA, opB);

        // Assert: B shifts back by A's length
        var result = (DeleteOperation)transformed;
        result.Position.Should().Be(5); // 10 - 5
        result.Length.Should().Be(3);
    }

    [Fact]
    public void Transform_DeleteDelete_NonOverlapping_BBeforeA_NoChange()
    {
        // Arrange: A deletes 10-15, B deletes 2-5
        var opA = new DeleteOperation { Position = 10, Length = 5 };
        var opB = new DeleteOperation { Position = 2, Length = 3 };

        // Act
        var transformed = _transformer.Transform(opA, opB);

        // Assert: B should not change
        var result = (DeleteOperation)transformed;
        result.Position.Should().Be(2);
        result.Length.Should().Be(3);
    }

    [Fact]
    public void Transform_DeleteDelete_AContainsB_BBecomesNoOp()
    {
        // Arrange: A deletes 5-15, B deletes 7-10 (entirely within A)
        var opA = new DeleteOperation { Position = 5, Length = 10 };
        var opB = new DeleteOperation { Position = 7, Length = 3 };

        // Act
        var transformed = _transformer.Transform(opA, opB);

        // Assert: B becomes a no-op (length 0)
        var result = (DeleteOperation)transformed;
        result.Length.Should().Be(0);
    }

    [Fact]
    public void Transform_DeleteDelete_BContainsA_BReducedByALength()
    {
        // Arrange: A deletes 7-10, B deletes 5-15 (B contains A)
        var opA = new DeleteOperation { Position = 7, Length = 3 };
        var opB = new DeleteOperation { Position = 5, Length = 10 };

        // Act
        var transformed = _transformer.Transform(opA, opB);

        // Assert: B's length reduced by A's length
        var result = (DeleteOperation)transformed;
        result.Position.Should().Be(5);
        result.Length.Should().Be(7); // 10 - 3
    }

    [Fact]
    public void Transform_DeleteDelete_PartialOverlap_AStartsFirst()
    {
        // Arrange: A deletes 5-10, B deletes 7-12
        var opA = new DeleteOperation { Position = 5, Length = 5 }; // 5-10
        var opB = new DeleteOperation { Position = 7, Length = 5 }; // 7-12

        // Act
        var transformed = _transformer.Transform(opA, opB);

        // Assert: B reduced by overlap (3 chars from 7-10)
        var result = (DeleteOperation)transformed;
        result.Position.Should().Be(5);
        result.Length.Should().Be(2); // Only 10-12 remains
    }

    [Fact]
    public void Transform_DeleteDelete_PartialOverlap_BStartsFirst()
    {
        // Arrange: A deletes 7-12, B deletes 5-10
        var opA = new DeleteOperation { Position = 7, Length = 5 }; // 7-12
        var opB = new DeleteOperation { Position = 5, Length = 5 }; // 5-10

        // Act
        var transformed = _transformer.Transform(opA, opB);

        // Assert: B reduced by overlap (3 chars from 7-10)
        var result = (DeleteOperation)transformed;
        result.Position.Should().Be(5);
        result.Length.Should().Be(2); // Only 5-7 remains
    }

    #endregion

    #region Apply Operation Tests

    [Fact]
    public void ApplyOperation_Insert_InsertsTextAtPosition()
    {
        // Arrange
        var content = "Hello World";
        var op = new InsertOperation { Position = 6, Text = "Beautiful " };

        // Act
        var result = _transformer.ApplyOperation(content, op);

        // Assert
        result.Should().Be("Hello Beautiful World");
    }

    [Fact]
    public void ApplyOperation_Delete_RemovesTextAtPosition()
    {
        // Arrange
        var content = "Hello Beautiful World";
        var op = new DeleteOperation { Position = 6, Length = 10 };

        // Act
        var result = _transformer.ApplyOperation(content, op);

        // Assert
        result.Should().Be("Hello World");
    }

    [Fact]
    public void ApplyOperation_InsertAtStart()
    {
        var content = "World";
        var op = new InsertOperation { Position = 0, Text = "Hello " };

        var result = _transformer.ApplyOperation(content, op);

        result.Should().Be("Hello World");
    }

    [Fact]
    public void ApplyOperation_InsertAtEnd()
    {
        var content = "Hello";
        var op = new InsertOperation { Position = 5, Text = " World" };

        var result = _transformer.ApplyOperation(content, op);

        result.Should().Be("Hello World");
    }

    [Fact]
    public void ApplyOperation_DeleteFromStart()
    {
        var content = "Hello World";
        var op = new DeleteOperation { Position = 0, Length = 6 };

        var result = _transformer.ApplyOperation(content, op);

        result.Should().Be("World");
    }

    [Fact]
    public void ApplyOperation_DeleteToEnd()
    {
        var content = "Hello World";
        var op = new DeleteOperation { Position = 5, Length = 6 };

        var result = _transformer.ApplyOperation(content, op);

        result.Should().Be("Hello");
    }

    #endregion

    #region Composite Operation Tests

    [Fact]
    public void ApplyOperation_Composite_AppliesAllOperationsInOrder()
    {
        // Arrange: Replace "World" with "Universe"
        var content = "Hello World";
        var composite = new CompositeOperation
        {
            Operations =
            [
                new DeleteOperation { Position = 6, Length = 5 },
                new InsertOperation { Position = 6, Text = "Universe" }
            ]
        };

        // Act
        var result = _transformer.ApplyOperation(content, composite);

        // Assert
        result.Should().Be("Hello Universe");
    }

    [Fact]
    public void Transform_CompositeFirst_TransformsAgainstAllOps()
    {
        // Arrange: A is composite, B is single insert
        var opA = new CompositeOperation
        {
            Operations =
            [
                new InsertOperation { Position = 0, Text = "A" },
                new InsertOperation { Position = 1, Text = "B" }
            ]
        };
        var opB = new InsertOperation { Position = 0, Text = "X" };

        // Act
        var transformed = _transformer.Transform(opA, opB);

        // Assert: B should shift by total length of A's inserts
        var result = (InsertOperation)transformed;
        result.Position.Should().Be(2); // Shifted by A (1) then B (1)
    }

    #endregion

    #region NoOp Tests

    [Fact]
    public void Transform_NoOpFirst_ReturnsBUnchanged()
    {
        var opA = new NoOpOperation();
        var opB = new InsertOperation { Position = 5, Text = "Test" };

        var result = _transformer.Transform(opA, opB);

        result.Should().BeEquivalentTo(opB);
    }

    [Fact]
    public void ApplyOperation_NoOp_ReturnsContentUnchanged()
    {
        var content = "Hello World";
        var op = new NoOpOperation();

        var result = _transformer.ApplyOperation(content, op);

        result.Should().Be("Hello World");
    }

    #endregion

    #region Concurrent Edit Simulation Tests

    [Fact]
    public void ConcurrentEdits_TwoInsertsAtSamePosition_BothApplied()
    {
        // Simulate: Both users try to insert at position 5
        var initialContent = "Hello World";
        var opA = new InsertOperation { Position = 6, Text = "A" };
        var opB = new InsertOperation { Position = 6, Text = "B" };

        // Server receives A first, then B
        // Apply A
        var afterA = _transformer.ApplyOperation(initialContent, opA);
        afterA.Should().Be("Hello AWorld");

        // Transform B against A
        var transformedB = _transformer.Transform(opA, opB);

        // Apply transformed B
        var afterB = _transformer.ApplyOperation(afterA, transformedB);
        afterB.Should().Be("Hello ABWorld");
    }

    [Fact]
    public void ConcurrentEdits_InsertAndDelete_BothApplied()
    {
        // User A inserts, User B deletes
        var initialContent = "Hello World";
        var opA = new InsertOperation { Position = 6, Text = "Beautiful " };
        var opB = new DeleteOperation { Position = 6, Length = 5 }; // Delete "World"

        // Apply A first
        var afterA = _transformer.ApplyOperation(initialContent, opA);
        afterA.Should().Be("Hello Beautiful World");

        // Transform B against A
        var transformedB = _transformer.Transform(opA, opB);

        // Apply transformed B
        var afterB = _transformer.ApplyOperation(afterA, transformedB);
        // B was trying to delete "World" which moved to position 16
        afterB.Should().Be("Hello Beautiful ");
    }

    [Fact]
    public void ConcurrentEdits_DeleteAndInsertInDeletedRange_InsertMovesToDeletePosition()
    {
        // User A deletes "World", User B tries to insert within "World"
        var initialContent = "Hello World!";
        var opA = new DeleteOperation { Position = 6, Length = 5 }; // Delete "World"
        var opB = new InsertOperation { Position = 8, Text = "X" }; // Insert in middle of "World"

        // Apply A first
        var afterA = _transformer.ApplyOperation(initialContent, opA);
        afterA.Should().Be("Hello !");

        // Transform B against A
        var transformedB = _transformer.Transform(opA, opB);

        // B's insert position should move to the delete position
        var result = (InsertOperation)transformedB;
        result.Position.Should().Be(6);

        // Apply transformed B
        var afterB = _transformer.ApplyOperation(afterA, transformedB);
        afterB.Should().Be("Hello X!");
    }

    #endregion
}
