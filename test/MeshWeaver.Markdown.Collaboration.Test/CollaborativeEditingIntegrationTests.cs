using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Markdown.Collaboration;
using Xunit;

namespace MeshWeaver.Markdown.Collaboration.Test;

/// <summary>
/// Integration tests for collaborative editing scenarios with multiple clients.
/// </summary>
public class CollaborativeEditingIntegrationTests
{
    private readonly CollaborativeEditingCoordinator _coordinator = new();

    #region Multi-Client Concurrent Editing

    [Fact]
    public void TwoClients_ConcurrentInserts_BothApplied()
    {
        // Arrange - Two clients editing the same document
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello World");

        // Client A types " Beautiful" after "Hello"
        var opA = new InsertOperation
        {
            DocumentId = docId,
            Position = 5,
            Text = " Beautiful",
            UserId = "clientA",
            BaseVersion = 0
        };

        // Client B types " Amazing" after "Hello" (also at position 5, based on version 0)
        var opB = new InsertOperation
        {
            DocumentId = docId,
            Position = 5,
            Text = " Amazing",
            UserId = "clientB",
            BaseVersion = 0
        };

        // Act - A applies first
        var responseA = _coordinator.ApplyOperation(docId, opA, "Hello World");
        // B applies second (should be transformed)
        var responseB = _coordinator.ApplyOperation(docId, opB, "Hello Beautiful World");

        // Assert
        responseA.Success.Should().BeTrue();
        responseB.Success.Should().BeTrue();
        responseA.NewVersion.Should().Be(1);
        responseB.NewVersion.Should().Be(2);

        var content = _coordinator.GetDocumentContent(docId);
        // Both inserts should be present in the final content
        content.Should().Contain("Beautiful");
        content.Should().Contain("Amazing");
    }

    [Fact]
    public void TwoClients_InsertAndDelete_TransformedCorrectly()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello Beautiful World");

        // Client A inserts "Very " before "Beautiful"
        var opA = new InsertOperation
        {
            DocumentId = docId,
            Position = 6,
            Text = "Very ",
            UserId = "clientA",
            BaseVersion = 0
        };

        // Client B deletes "Beautiful " (positions 6-16 in original)
        var opB = new DeleteOperation
        {
            DocumentId = docId,
            Position = 6,
            Length = 10,
            UserId = "clientB",
            BaseVersion = 0
        };

        // Act
        var responseA = _coordinator.ApplyOperation(docId, opA, "Hello Beautiful World");
        var responseB = _coordinator.ApplyOperation(docId, opB, "Hello Very Beautiful World");

        // Assert
        responseA.Success.Should().BeTrue();
        responseB.Success.Should().BeTrue();

        var content = _coordinator.GetDocumentContent(docId);
        // "Very " should be kept (inserted by A)
        content.Should().Contain("Very");
        // "Beautiful " should be deleted (by B, after transformation)
        content.Should().NotContain("Beautiful");
    }

    [Fact]
    public void ThreeClients_ConcurrentEdits_AllApplied()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "The quick fox");

        // All clients work on version 0
        var opA = new InsertOperation
        {
            DocumentId = docId,
            Position = 4,
            Text = "very ",
            UserId = "clientA",
            BaseVersion = 0
        };

        var opB = new InsertOperation
        {
            DocumentId = docId,
            Position = 10,
            Text = "brown ",
            UserId = "clientB",
            BaseVersion = 0
        };

        var opC = new InsertOperation
        {
            DocumentId = docId,
            Position = 13,
            Text = " jumps",
            UserId = "clientC",
            BaseVersion = 0
        };

        // Act - Apply in order A, B, C
        _coordinator.ApplyOperation(docId, opA, "The quick fox");
        _coordinator.ApplyOperation(docId, opB, "The very quick fox");
        _coordinator.ApplyOperation(docId, opC, "The very quick brown fox");

        // Assert
        var content = _coordinator.GetDocumentContent(docId);
        content.Should().Contain("very");
        content.Should().Contain("brown");
        content.Should().Contain("jumps");

        var state = _coordinator.GetDocumentState(docId);
        state!.Version.Should().Be(3);
    }

    #endregion

    #region Comment Marker Survival

    [Fact]
    public void Comment_SurvivesEdits_MarkerShiftsWithContent()
    {
        // Arrange - Document with a comment marker
        var docId = "doc1";
        var initialContent = "Hello <!--comment:c1-->world<!--/comment:c1--> there";
        _coordinator.InitializeDocument(docId, initialContent);

        // Insert text before the comment
        var op = new InsertOperation
        {
            DocumentId = docId,
            Position = 0,
            Text = "Well, ",
            UserId = "user1",
            BaseVersion = 0
        };

        // Act
        _coordinator.ApplyOperation(docId, op, initialContent);

        // Assert
        var content = _coordinator.GetDocumentContent(docId);
        content.Should().StartWith("Well, Hello");

        // Comment markers should still be in the content
        var comments = MarkdownAnnotationParser.ExtractComments(content);
        comments.Should().HaveCount(1);
        comments[0].MarkerId.Should().Be("c1");
        comments[0].AnnotatedText.Should().Be("world");
    }

    [Fact]
    public void Comment_InsertWithinCommentedRange_PreservesMarkers()
    {
        // Arrange
        var docId = "doc1";
        var initialContent = "Hello <!--comment:c1-->beautiful world<!--/comment:c1-->!";
        _coordinator.InitializeDocument(docId, initialContent);

        // Insert "very " within the commented text (after "beautiful ")
        // Position is: "Hello <!--comment:c1-->beautiful " = 33 chars
        var op = new InsertOperation
        {
            DocumentId = docId,
            Position = 33,
            Text = "very ",
            UserId = "user1",
            BaseVersion = 0
        };

        // Act
        _coordinator.ApplyOperation(docId, op, initialContent);

        // Assert
        var content = _coordinator.GetDocumentContent(docId);
        var comments = MarkdownAnnotationParser.ExtractComments(content!);
        comments.Should().HaveCount(1);
        // The annotated text should now include "very "
        comments[0].AnnotatedText.Should().Contain("very");
    }

    [Fact]
    public void MultipleComments_ConcurrentEdits_AllPreserved()
    {
        // Arrange
        var docId = "doc1";
        var initialContent = "<!--comment:c1-->First<!--/comment:c1--> and <!--comment:c2-->Second<!--/comment:c2-->";
        _coordinator.InitializeDocument(docId, initialContent);

        // Insert between comments
        var op = new InsertOperation
        {
            DocumentId = docId,
            Position = 40, // After "First" comment
            Text = " also ",
            UserId = "user1",
            BaseVersion = 0
        };

        // Act
        _coordinator.ApplyOperation(docId, op, initialContent);

        // Assert
        var content = _coordinator.GetDocumentContent(docId);
        var comments = MarkdownAnnotationParser.ExtractComments(content!);
        comments.Should().HaveCount(2);
        comments.Should().Contain(c => c.MarkerId == "c1");
        comments.Should().Contain(c => c.MarkerId == "c2");
    }

    #endregion

    #region Track Change Workflows

    [Fact]
    public void TrackChange_AcceptInsertion_RemovesMarkers_KeepsText()
    {
        // Arrange - Document with a suggested insertion
        var content = "Hello <!--insert:i1-->beautiful <!--/insert:i1-->world!";

        // Act - Accept the insertion (keep text, remove markers)
        var result = MarkdownAnnotationParser.RemoveMarkers(content, "i1");

        // Assert
        result.Should().Be("Hello beautiful world!");
    }

    [Fact]
    public void TrackChange_RejectInsertion_RemovesMarkersAndText()
    {
        // Arrange
        var content = "Hello <!--insert:i1-->ugly <!--/insert:i1-->world!";

        // Act - Reject the insertion (remove both markers and text)
        var result = MarkdownAnnotationParser.RemoveMarkersAndContent(content, "i1");

        // Assert
        result.Should().Be("Hello world!");
    }

    [Fact]
    public void TrackChange_AcceptDeletion_RemovesMarkersAndText()
    {
        // Arrange - Document with text marked for deletion
        var content = "Hello <!--delete:d1-->ugly <!--/delete:d1-->world!";

        // Act - Accept the deletion (remove the deleted text)
        var result = MarkdownAnnotationParser.RemoveMarkersAndContent(content, "d1");

        // Assert
        result.Should().Be("Hello world!");
    }

    [Fact]
    public void TrackChange_RejectDeletion_RemovesMarkers_KeepsText()
    {
        // Arrange
        var content = "Hello <!--delete:d1-->beautiful <!--/delete:d1-->world!";

        // Act - Reject the deletion (keep the text)
        var result = MarkdownAnnotationParser.RemoveMarkers(content, "d1");

        // Assert
        result.Should().Be("Hello beautiful world!");
    }

    [Fact]
    public void TrackChange_MultipleChanges_AcceptAll()
    {
        // Arrange - Multiple track changes
        var content = "<!--insert:i1-->New <!--/insert:i1-->text with <!--delete:d1-->old <!--/delete:d1-->content";

        // Act - Accept all changes
        // Accept insertion = remove markers only
        var step1 = MarkdownAnnotationParser.RemoveMarkers(content, "i1");
        // Accept deletion = remove markers and content
        var result = MarkdownAnnotationParser.RemoveMarkersAndContent(step1, "d1");

        // Assert
        result.Should().Be("New text with content");
    }

    [Fact]
    public void TrackChange_MultipleChanges_RejectAll()
    {
        // Arrange
        var content = "<!--insert:i1-->New <!--/insert:i1-->text with <!--delete:d1-->old <!--/delete:d1-->content";

        // Act - Reject all changes
        // Reject insertion = remove markers and content
        var step1 = MarkdownAnnotationParser.RemoveMarkersAndContent(content, "i1");
        // Reject deletion = remove markers only (keep text)
        var result = MarkdownAnnotationParser.RemoveMarkers(step1, "d1");

        // Assert
        result.Should().Be("text with old content");
    }

    #endregion

    #region Client Reconnection

    [Fact]
    public void Client_Reconnect_ReceivesPendingOperations()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello World");

        // Apply several operations
        _coordinator.ApplyOperation(docId, new InsertOperation
        {
            DocumentId = docId,
            Position = 5,
            Text = " Beautiful",
            UserId = "user1",
            BaseVersion = 0
        }, "Hello World");

        _coordinator.ApplyOperation(docId, new InsertOperation
        {
            DocumentId = docId,
            Position = 16,
            Text = " Amazing",
            UserId = "user2",
            BaseVersion = 1
        }, "Hello Beautiful World");

        // Act - Client reconnects with version 0 (missed both operations)
        var pendingOps = _coordinator.GetOperationsSince(docId, 0);

        // Assert
        pendingOps.Should().HaveCount(2);
        pendingOps.Should().AllSatisfy(op => op.BaseVersion.Should().BeGreaterThan(0));
    }

    [Fact]
    public void Client_Reconnect_GetLatestContent()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Original content");

        _coordinator.ApplyOperation(docId, new InsertOperation
        {
            DocumentId = docId,
            Position = 0,
            Text = "Modified: ",
            UserId = "user1",
            BaseVersion = 0
        }, "Original content");

        // Act - Reconnecting client gets current content
        var content = _coordinator.GetDocumentContent(docId);
        var state = _coordinator.GetDocumentState(docId);

        // Assert
        content.Should().Be("Modified: Original content");
        state!.Version.Should().Be(1);
    }

    #endregion

    #region Session Presence

    [Fact]
    public void MultipleSessions_TrackPresence()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello World");

        var session1 = new EditingSession
        {
            SessionId = "s1",
            UserId = "user1",
            DisplayName = "User One",
            Color = "#ff0000"
        };

        var session2 = new EditingSession
        {
            SessionId = "s2",
            UserId = "user2",
            DisplayName = "User Two",
            Color = "#00ff00"
        };

        // Act
        _coordinator.RegisterSession(docId, session1);
        _coordinator.RegisterSession(docId, session2);

        // Assert
        var sessions = _coordinator.GetActiveSessions(docId);
        sessions.Should().HaveCount(2);
        sessions.Should().Contain(s => s.UserId == "user1");
        sessions.Should().Contain(s => s.UserId == "user2");
    }

    [Fact]
    public void Session_CursorUpdates_ReflectInPresence()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello World");

        _coordinator.RegisterSession(docId, new EditingSession
        {
            SessionId = "s1",
            UserId = "user1",
            CursorPosition = 0
        });

        // Act - User moves cursor
        _coordinator.UpdateSessionCursor(docId, "s1", 5, null, null);

        // Assert
        var sessions = _coordinator.GetActiveSessions(docId);
        sessions[0].CursorPosition.Should().Be(5);
    }

    [Fact]
    public void Session_Selection_ReflectInPresence()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello World");

        _coordinator.RegisterSession(docId, new EditingSession
        {
            SessionId = "s1",
            UserId = "user1"
        });

        // Act - User selects text
        _coordinator.UpdateSessionCursor(docId, "s1", 0, 0, 5);

        // Assert
        var sessions = _coordinator.GetActiveSessions(docId);
        sessions[0].SelectionStart.Should().Be(0);
        sessions[0].SelectionEnd.Should().Be(5);
    }

    #endregion

    #region Vector Clock

    [Fact]
    public void VectorClock_TracksAllUsers()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello");

        // Act
        _coordinator.ApplyOperation(docId, new InsertOperation
        {
            DocumentId = docId,
            Position = 5,
            Text = " A",
            UserId = "userA",
            BaseVersion = 0
        }, "Hello");

        _coordinator.ApplyOperation(docId, new InsertOperation
        {
            DocumentId = docId,
            Position = 7,
            Text = " B",
            UserId = "userB",
            BaseVersion = 1
        }, "Hello A");

        _coordinator.ApplyOperation(docId, new InsertOperation
        {
            DocumentId = docId,
            Position = 9,
            Text = " A2",
            UserId = "userA",
            BaseVersion = 2
        }, "Hello A B");

        // Assert
        var state = _coordinator.GetDocumentState(docId);
        state!.VectorClock.Should().ContainKey("userA");
        state.VectorClock.Should().ContainKey("userB");
        state.VectorClock["userA"].Should().Be(2); // userA made 2 edits
        state.VectorClock["userB"].Should().Be(1); // userB made 1 edit
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void EmptyDocument_MultipleInserts()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "");

        // Act
        _coordinator.ApplyOperation(docId, new InsertOperation
        {
            DocumentId = docId,
            Position = 0,
            Text = "Hello",
            UserId = "user1",
            BaseVersion = 0
        }, "");

        _coordinator.ApplyOperation(docId, new InsertOperation
        {
            DocumentId = docId,
            Position = 5,
            Text = " World",
            UserId = "user2",
            BaseVersion = 1
        }, "Hello");

        // Assert
        _coordinator.GetDocumentContent(docId).Should().Be("Hello World");
    }

    [Fact]
    public void LargeDocument_ConcurrentEditsAtDifferentPositions()
    {
        // Arrange
        var docId = "doc1";
        var initialContent = new string('x', 10000);
        _coordinator.InitializeDocument(docId, initialContent);

        // Act - Edits at very different positions (should not conflict)
        var op1 = new InsertOperation
        {
            DocumentId = docId,
            Position = 100,
            Text = "A",
            UserId = "user1",
            BaseVersion = 0
        };

        var op2 = new InsertOperation
        {
            DocumentId = docId,
            Position = 9000,
            Text = "B",
            UserId = "user2",
            BaseVersion = 0
        };

        _coordinator.ApplyOperation(docId, op1, initialContent);
        _coordinator.ApplyOperation(docId, op2, initialContent);

        // Assert
        var content = _coordinator.GetDocumentContent(docId);
        content!.Length.Should().Be(10002); // Original + 2 inserts
        content[100].Should().Be('A');
        content[9001].Should().Be('B'); // Shifted by 1 due to first insert
    }

    [Fact]
    public void RapidSequentialEdits_FromSameUser()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "");

        // Act - Simulate typing character by character
        for (int i = 0; i < 10; i++)
        {
            _coordinator.ApplyOperation(docId, new InsertOperation
            {
                DocumentId = docId,
                Position = i,
                Text = ((char)('a' + i)).ToString(),
                UserId = "user1",
                BaseVersion = i
            }, "");
        }

        // Assert
        _coordinator.GetDocumentContent(docId).Should().Be("abcdefghij");
        _coordinator.GetDocumentState(docId)!.Version.Should().Be(10);
    }

    [Fact]
    public void DeleteEntireContent_ThenReinsert()
    {
        // Arrange
        var docId = "doc1";
        _coordinator.InitializeDocument(docId, "Hello World");

        // Act - Delete all content
        _coordinator.ApplyOperation(docId, new DeleteOperation
        {
            DocumentId = docId,
            Position = 0,
            Length = 11,
            UserId = "user1",
            BaseVersion = 0
        }, "Hello World");

        // Insert new content
        _coordinator.ApplyOperation(docId, new InsertOperation
        {
            DocumentId = docId,
            Position = 0,
            Text = "New Content",
            UserId = "user1",
            BaseVersion = 1
        }, "");

        // Assert
        _coordinator.GetDocumentContent(docId).Should().Be("New Content");
    }

    #endregion
}
