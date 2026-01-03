using System;
using FluentAssertions;
using MeshWeaver.Markdown.Collaboration;
using Xunit;

namespace MeshWeaver.Markdown.Collaboration.Test;

public class CollaborativeEditingCoordinatorTests
{
    private readonly CollaborativeEditingCoordinator _coordinator = new();

    #region Basic Operations

    [Fact]
    public void ApplyOperation_SingleInsert_UpdatesContent()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello World");

        var op = new InsertOperation
        {
            DocumentId = docId,
            Position = 6,
            Text = "Beautiful ",
            UserId = "user1",
            BaseVersion = 0
        };

        // Act
        var response = _coordinator.ApplyOperation(docId, op, "Hello World");

        // Assert
        response.Success.Should().BeTrue();
        response.NewVersion.Should().Be(1);
        _coordinator.GetDocumentContent(docId).Should().Be("Hello Beautiful World");
    }

    [Fact]
    public void ApplyOperation_SingleDelete_UpdatesContent()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello Beautiful World");

        var op = new DeleteOperation
        {
            DocumentId = docId,
            Position = 6,
            Length = 10,
            UserId = "user1",
            BaseVersion = 0
        };

        // Act
        var response = _coordinator.ApplyOperation(docId, op, "Hello Beautiful World");

        // Assert
        response.Success.Should().BeTrue();
        _coordinator.GetDocumentContent(docId).Should().Be("Hello World");
    }

    [Fact]
    public void ApplyOperation_MultipleSequential_UpdatesVersionCorrectly()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello");

        // Act
        var response1 = _coordinator.ApplyOperation(docId, new InsertOperation
        {
            DocumentId = docId,
            Position = 5,
            Text = " World",
            UserId = "user1",
            BaseVersion = 0
        }, "Hello");

        var response2 = _coordinator.ApplyOperation(docId, new InsertOperation
        {
            DocumentId = docId,
            Position = 11,
            Text = "!",
            UserId = "user1",
            BaseVersion = 1
        }, "");

        // Assert
        response1.NewVersion.Should().Be(1);
        response2.NewVersion.Should().Be(2);
        _coordinator.GetDocumentContent(docId).Should().Be("Hello World!");
    }

    #endregion

    #region Concurrent Editing

    [Fact]
    public void ApplyOperation_ConcurrentInserts_TransformsCorrectly()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello World");

        // User A inserts at position 6
        var opA = new InsertOperation
        {
            DocumentId = docId,
            Position = 6,
            Text = "A",
            UserId = "userA",
            BaseVersion = 0
        };

        // User B also tries to insert at position 6, but based on version 0
        var opB = new InsertOperation
        {
            DocumentId = docId,
            Position = 6,
            Text = "B",
            UserId = "userB",
            BaseVersion = 0
        };

        // Act - A is applied first
        var responseA = _coordinator.ApplyOperation(docId, opA, "Hello World");

        // B is applied after A, should be transformed
        var responseB = _coordinator.ApplyOperation(docId, opB, "Hello AWorld");

        // Assert
        responseA.Success.Should().BeTrue();
        responseB.Success.Should().BeTrue();

        // Both inserts should be in the final content
        var content = _coordinator.GetDocumentContent(docId);
        content.Should().Contain("A");
        content.Should().Contain("B");
    }

    [Fact]
    public void ApplyOperation_ConcurrentInsertAndDelete_TransformsCorrectly()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello World");

        // User A inserts "Beautiful " at position 6
        var opA = new InsertOperation
        {
            DocumentId = docId,
            Position = 6,
            Text = "Beautiful ",
            UserId = "userA",
            BaseVersion = 0
        };

        // User B tries to delete "World" (position 6, length 5) based on version 0
        var opB = new DeleteOperation
        {
            DocumentId = docId,
            Position = 6,
            Length = 5,
            UserId = "userB",
            BaseVersion = 0
        };

        // Act
        var responseA = _coordinator.ApplyOperation(docId, opA, "Hello World");
        var responseB = _coordinator.ApplyOperation(docId, opB, "");

        // Assert
        responseA.Success.Should().BeTrue();
        responseB.Success.Should().BeTrue();

        // "World" should be deleted from the transformed position
        var content = _coordinator.GetDocumentContent(docId);
        content.Should().Contain("Beautiful");
        content.Should().NotContain("World");
    }

    #endregion

    #region Document State

    [Fact]
    public void GetDocumentState_ReturnsCorrectState()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello");
        _coordinator.ApplyOperation(docId, new InsertOperation
        {
            DocumentId = docId,
            Position = 5,
            Text = " World",
            UserId = "user1",
            BaseVersion = 0
        }, "Hello");

        // Act
        var state = _coordinator.GetDocumentState(docId);

        // Assert
        state.Should().NotBeNull();
        state!.DocumentId.Should().Be(docId);
        state.Version.Should().Be(1);
        state.VectorClock.Should().ContainKey("user1");
    }

    [Fact]
    public void GetOperationsSince_ReturnsCorrectOperations()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello");

        _coordinator.ApplyOperation(docId, new InsertOperation
        {
            DocumentId = docId,
            Position = 5,
            Text = " A",
            UserId = "user1",
            BaseVersion = 0
        }, "Hello");

        _coordinator.ApplyOperation(docId, new InsertOperation
        {
            DocumentId = docId,
            Position = 7,
            Text = " B",
            UserId = "user2",
            BaseVersion = 1
        }, "");

        // Act
        var ops = _coordinator.GetOperationsSince(docId, 0);

        // Assert
        ops.Should().HaveCount(2);
    }

    #endregion

    #region Session Management

    [Fact]
    public void RegisterSession_AddsSession()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello");

        var session = new EditingSession
        {
            SessionId = "session1",
            UserId = "user1",
            DisplayName = "User One",
            Color = "#ff0000"
        };

        // Act
        _coordinator.RegisterSession(docId, session);

        // Assert
        var sessions = _coordinator.GetActiveSessions(docId);
        sessions.Should().HaveCount(1);
        sessions[0].UserId.Should().Be("user1");
    }

    [Fact]
    public void UpdateSessionCursor_UpdatesPosition()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello World");

        var session = new EditingSession
        {
            SessionId = "session1",
            UserId = "user1",
            CursorPosition = 0
        };
        _coordinator.RegisterSession(docId, session);

        // Act
        _coordinator.UpdateSessionCursor(docId, "session1", 5, 5, 10);

        // Assert
        var sessions = _coordinator.GetActiveSessions(docId);
        sessions[0].CursorPosition.Should().Be(5);
        sessions[0].SelectionStart.Should().Be(5);
        sessions[0].SelectionEnd.Should().Be(10);
    }

    [Fact]
    public void UnregisterSession_RemovesSession()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello");

        _coordinator.RegisterSession(docId, new EditingSession
        {
            SessionId = "session1",
            UserId = "user1"
        });

        // Act
        _coordinator.UnregisterSession(docId, "session1");

        // Assert
        _coordinator.GetActiveSessions(docId).Should().BeEmpty();
    }

    [Fact]
    public void CleanupStaleSessions_RemovesInactiveSessions()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello");

        var staleSession = new EditingSession
        {
            SessionId = "stale",
            UserId = "user1",
            LastActivity = DateTimeOffset.UtcNow.AddMinutes(-10)
        };

        var activeSession = new EditingSession
        {
            SessionId = "active",
            UserId = "user2",
            LastActivity = DateTimeOffset.UtcNow
        };

        _coordinator.RegisterSession(docId, staleSession);
        _coordinator.RegisterSession(docId, activeSession);

        // Act
        _coordinator.CleanupStaleSessions(TimeSpan.FromMinutes(5));

        // Assert
        var sessions = _coordinator.GetActiveSessions(docId);
        sessions.Should().HaveCount(1);
        sessions[0].SessionId.Should().Be("active");
    }

    #endregion

    #region Error Handling

    [Fact]
    public void ApplyOperation_InvalidPosition_ReturnsError()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello");

        var op = new InsertOperation
        {
            DocumentId = docId,
            Position = 100, // Invalid - beyond content length
            Text = "X",
            UserId = "user1",
            BaseVersion = 0
        };

        // Act
        var response = _coordinator.ApplyOperation(docId, op, "Hello");

        // Assert
        response.Success.Should().BeFalse();
        response.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDocumentState_NonExistentDocument_ReturnsNull()
    {
        var state = _coordinator.GetDocumentState("nonexistent");

        state.Should().BeNull();
    }

    [Fact]
    public void GetDocumentContent_NonExistentDocument_ReturnsNull()
    {
        var content = _coordinator.GetDocumentContent("nonexistent");

        content.Should().BeNull();
    }

    #endregion

    #region Remove Document

    [Fact]
    public void RemoveDocument_ExistingDocument_ReturnsTrue()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello");

        // Act
        var result = _coordinator.RemoveDocument(docId);

        // Assert
        result.Should().BeTrue();
        _coordinator.GetDocumentContent(docId).Should().BeNull();
    }

    [Fact]
    public void RemoveDocument_NonExistentDocument_ReturnsFalse()
    {
        var result = _coordinator.RemoveDocument("nonexistent");

        result.Should().BeFalse();
    }

    #endregion
}
