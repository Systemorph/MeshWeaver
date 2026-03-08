using System;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for Approval, Notification data models, node type registration,
/// extension methods, and related infrastructure.
/// </summary>
public class ApprovalAndNotificationTest
{
    #region Approval Data Model Tests

    [Fact]
    public void Approval_HasPrimaryNodePath()
    {
        var approval = new Approval
        {
            Purpose = "Review document",
            PrimaryNodePath = "docs/readme"
        };

        approval.PrimaryNodePath.Should().Be("docs/readme");
    }

    [Fact]
    public void Approval_DefaultValues_AreCorrect()
    {
        var approval = new Approval();

        approval.Id.Should().NotBeNullOrEmpty();
        approval.Requester.Should().BeEmpty();
        approval.Approver.Should().BeEmpty();
        approval.Purpose.Should().BeEmpty();
        approval.Status.Should().Be(ApprovalStatus.Pending);
        approval.DueDate.Should().BeNull();
        approval.ApprovalDate.Should().BeNull();
        approval.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Approval_CanBeCreated_WithAllProperties()
    {
        var dueDate = DateTimeOffset.UtcNow.AddDays(7);
        var approval = new Approval
        {
            Id = "test-id",
            PrimaryNodePath = "org/project/doc",
            Requester = "user1",
            Approver = "user2",
            Purpose = "Final review",
            DueDate = dueDate,
            Status = ApprovalStatus.Pending
        };

        approval.Id.Should().Be("test-id");
        approval.PrimaryNodePath.Should().Be("org/project/doc");
        approval.Requester.Should().Be("user1");
        approval.Approver.Should().Be("user2");
        approval.Purpose.Should().Be("Final review");
        approval.DueDate.Should().Be(dueDate);
        approval.Status.Should().Be(ApprovalStatus.Pending);
    }

    [Fact]
    public void Approval_Status_CanTransition_ToApproved()
    {
        var approval = new Approval { Status = ApprovalStatus.Pending };
        var approved = approval with
        {
            Status = ApprovalStatus.Approved,
            ApprovalDate = DateTimeOffset.UtcNow
        };

        approved.Status.Should().Be(ApprovalStatus.Approved);
        approved.ApprovalDate.Should().NotBeNull();
    }

    [Fact]
    public void Approval_Status_CanTransition_ToRejected()
    {
        var approval = new Approval { Status = ApprovalStatus.Pending };
        var rejected = approval with
        {
            Status = ApprovalStatus.Rejected,
            ApprovalDate = DateTimeOffset.UtcNow
        };

        rejected.Status.Should().Be(ApprovalStatus.Rejected);
        rejected.ApprovalDate.Should().NotBeNull();
    }

    [Fact]
    public void Approval_Record_WithExpression_PreservesOtherProperties()
    {
        var original = new Approval
        {
            Id = "abc",
            PrimaryNodePath = "docs/readme",
            Requester = "user1",
            Approver = "user2",
            Purpose = "Review"
        };

        var updated = original with { Status = ApprovalStatus.Approved };

        updated.Id.Should().Be("abc");
        updated.PrimaryNodePath.Should().Be("docs/readme");
        updated.Requester.Should().Be("user1");
        updated.Approver.Should().Be("user2");
        updated.Purpose.Should().Be("Review");
        updated.Status.Should().Be(ApprovalStatus.Approved);
    }

    #endregion

    #region Notification Data Model Tests

    [Fact]
    public void Notification_DefaultValues_AreCorrect()
    {
        var notification = new Notification();

        notification.Id.Should().NotBeNullOrEmpty();
        notification.Title.Should().BeEmpty();
        notification.Message.Should().BeEmpty();
        notification.TargetNodePath.Should().BeNull();
        notification.IsRead.Should().BeFalse();
        notification.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        notification.NotificationType.Should().Be(NotificationType.ApprovalRequired);
        notification.CreatedBy.Should().BeNull();
    }

    [Fact]
    public void Notification_IsNotSatelliteContent()
    {
        // Notification is not satellite content - it lives independently
        var node = new MeshNode("notif1", "User/user1") { Content = new Notification() };
        (node.MainNode != node.Path).Should().BeFalse();
    }

    [Fact]
    public void Notification_CanBeCreated_WithAllProperties()
    {
        var notification = new Notification
        {
            Id = "notif-1",
            Title = "Approval Requested",
            Message = "User1 requested your approval",
            TargetNodePath = "org/project/doc",
            IsRead = false,
            NotificationType = NotificationType.ApprovalRequired,
            CreatedBy = "user1"
        };

        notification.Id.Should().Be("notif-1");
        notification.Title.Should().Be("Approval Requested");
        notification.Message.Should().Be("User1 requested your approval");
        notification.TargetNodePath.Should().Be("org/project/doc");
        notification.IsRead.Should().BeFalse();
        notification.NotificationType.Should().Be(NotificationType.ApprovalRequired);
        notification.CreatedBy.Should().Be("user1");
    }

    [Fact]
    public void Notification_CanToggleReadStatus()
    {
        var notification = new Notification { IsRead = false };
        var read = notification with { IsRead = true };

        read.IsRead.Should().BeTrue();
    }

    [Theory]
    [InlineData(NotificationType.ApprovalRequired)]
    [InlineData(NotificationType.ApprovalGiven)]
    [InlineData(NotificationType.ApprovalRejected)]
    [InlineData(NotificationType.General)]
    public void Notification_AllTypes_AreValid(NotificationType type)
    {
        var notification = new Notification { NotificationType = type };
        notification.NotificationType.Should().Be(type);
    }

    #endregion

    #region ApprovalStatus Enum Tests

    [Fact]
    public void ApprovalStatus_HasExpectedValues()
    {
        Enum.GetValues<ApprovalStatus>().Should().HaveCount(3);
        Enum.GetValues<ApprovalStatus>().Should().Contain(ApprovalStatus.Pending);
        Enum.GetValues<ApprovalStatus>().Should().Contain(ApprovalStatus.Approved);
        Enum.GetValues<ApprovalStatus>().Should().Contain(ApprovalStatus.Rejected);
    }

    #endregion

    #region NotificationType Enum Tests

    [Fact]
    public void NotificationType_HasExpectedValues()
    {
        Enum.GetValues<NotificationType>().Should().HaveCount(4);
        Enum.GetValues<NotificationType>().Should().Contain(NotificationType.ApprovalRequired);
        Enum.GetValues<NotificationType>().Should().Contain(NotificationType.ApprovalGiven);
        Enum.GetValues<NotificationType>().Should().Contain(NotificationType.ApprovalRejected);
        Enum.GetValues<NotificationType>().Should().Contain(NotificationType.General);
    }

    #endregion

    #region Node Type Configuration Tests

    [Fact]
    public void ApprovalNodeType_HasCorrectNodeType()
    {
        ApprovalNodeType.NodeType.Should().Be("Approval");
    }

    [Fact]
    public void ApprovalNodeType_CreateMeshNode_HasCorrectProperties()
    {
        var node = ApprovalNodeType.CreateMeshNode();

        node.Name.Should().Be("Approval");
        node.Icon.Should().Contain("checkmark.svg");
        node.ExcludeFromContext.Should().Contain("search");
        node.ExcludeFromContext.Should().Contain("create");
        node.AssemblyLocation.Should().NotBeNullOrEmpty();
        node.HubConfiguration.Should().NotBeNull();
    }

    [Fact]
    public void NotificationNodeType_HasCorrectNodeType()
    {
        NotificationNodeType.NodeType.Should().Be("Notification");
    }

    [Fact]
    public void NotificationNodeType_CreateMeshNode_HasCorrectProperties()
    {
        var node = NotificationNodeType.CreateMeshNode();

        node.Name.Should().Be("Notification");
        node.Icon.Should().Contain("bell.svg");
        node.ExcludeFromContext.Should().Contain("search");
        node.ExcludeFromContext.Should().Contain("create");
        node.AssemblyLocation.Should().NotBeNullOrEmpty();
        node.HubConfiguration.Should().NotBeNull();
    }

    #endregion

    #region MeshNode Integration Tests

    [Fact]
    public void MeshNode_WithMainNode_GetPrimaryPath_ReturnsMainNode()
    {
        var node = new MeshNode("approval1", "org/project/doc/_approvals")
        {
            MainNode = "org/project/doc",
            NodeType = ApprovalNodeType.NodeType
        };

        node.GetPrimaryPath().Should().Be("org/project/doc");
    }

    [Fact]
    public void MeshNode_WithNullMainNode_GetPrimaryPath_ReturnsNodePath()
    {
        var node = new MeshNode("approval1", "some/path");

        node.GetPrimaryPath().Should().Be("some/path/approval1");
    }

    [Fact]
    public void MeshNode_WithNotificationContent_IsNotSatellite()
    {
        var notification = new Notification { Title = "Test" };
        var node = new MeshNode("notif1", "User/user1") { Content = notification };

        // Notification is not satellite content, so GetPrimaryPath returns node's own path
        node.GetPrimaryPath().Should().Be("User/user1/notif1");
    }

    #endregion

    #region Type Registry Tests

    [Fact]
    public void WithGraphTypes_RegistersApprovalTypes()
    {
        var registry = new TestTypeRegistry();
        registry.WithGraphTypes();

        registry.GetType(nameof(Approval)).Should().Be(typeof(Approval));
        registry.GetType(nameof(ApprovalStatus)).Should().Be(typeof(ApprovalStatus));
    }

    [Fact]
    public void WithGraphTypes_RegistersNotificationTypes()
    {
        var registry = new TestTypeRegistry();
        registry.WithGraphTypes();

        registry.GetType(nameof(Notification)).Should().Be(typeof(Notification));
        registry.GetType(nameof(NotificationType)).Should().Be(typeof(NotificationType));
    }

    [Fact]
    public void WithGraphTypes_RegistersAllExpectedTypes()
    {
        var registry = new TestTypeRegistry();
        registry.WithGraphTypes();

        // Verify all expected types are registered (including pre-existing ones)
        registry.GetType(nameof(Approval)).Should().NotBeNull();
        registry.GetType(nameof(ApprovalStatus)).Should().NotBeNull();
        registry.GetType(nameof(Notification)).Should().NotBeNull();
        registry.GetType(nameof(NotificationType)).Should().NotBeNull();
        registry.GetType(nameof(Comment)).Should().NotBeNull();
    }

    #endregion

    #region AccessObject / AccessContext IsVirtual Tests

    [Fact]
    public void AccessObject_IsVirtual_DefaultsFalse()
    {
        var obj = new AccessObject();
        obj.IsVirtual.Should().BeFalse();
    }

    [Fact]
    public void AccessObject_IsVirtual_CanBeSetTrue()
    {
        var obj = new AccessObject { IsVirtual = true, Name = "Guest" };
        obj.IsVirtual.Should().BeTrue();
        obj.Name.Should().Be("Guest");
    }

    [Fact]
    public void AccessContext_IsVirtual_DefaultsFalse()
    {
        var ctx = new AccessContext();
        ctx.IsVirtual.Should().BeFalse();
    }

    [Fact]
    public void AccessContext_IsVirtual_CanBeSetTrue()
    {
        var ctx = new AccessContext
        {
            ObjectId = "guest-123",
            Name = "Guest",
            IsVirtual = true
        };
        ctx.IsVirtual.Should().BeTrue();
        ctx.ObjectId.Should().Be("guest-123");
    }

    [Fact]
    public void AccessContext_RealUser_IsNotVirtual()
    {
        var ctx = new AccessContext
        {
            ObjectId = "user@example.com",
            Name = "John Doe",
            Email = "john@example.com",
            IsVirtual = false
        };

        ctx.IsVirtual.Should().BeFalse();
        ctx.ObjectId.Should().NotBeEmpty();
    }

    #endregion

    #region Approval Workflow Integration Tests

    [Fact]
    public void Approval_FullWorkflow_PendingToApproved()
    {
        // Simulates the full approval workflow
        var approval = new Approval
        {
            Id = "workflow-test",
            PrimaryNodePath = "org/doc",
            Requester = "alice",
            Approver = "bob",
            Purpose = "Publication review",
            DueDate = DateTimeOffset.UtcNow.AddDays(3)
        };

        // Initial state
        approval.Status.Should().Be(ApprovalStatus.Pending);
        approval.ApprovalDate.Should().BeNull();

        // Bob approves
        var approvedAt = DateTimeOffset.UtcNow;
        var approved = approval with
        {
            Status = ApprovalStatus.Approved,
            ApprovalDate = approvedAt
        };

        approved.Status.Should().Be(ApprovalStatus.Approved);
        approved.ApprovalDate.Should().Be(approvedAt);
        approved.Requester.Should().Be("alice");
        approved.Approver.Should().Be("bob");
        approved.Purpose.Should().Be("Publication review");
    }

    [Fact]
    public void Approval_FullWorkflow_PendingToRejected()
    {
        var approval = new Approval
        {
            Id = "rejection-test",
            PrimaryNodePath = "org/doc",
            Requester = "alice",
            Approver = "bob",
            Purpose = "Budget approval"
        };

        var rejectedAt = DateTimeOffset.UtcNow;
        var rejected = approval with
        {
            Status = ApprovalStatus.Rejected,
            ApprovalDate = rejectedAt
        };

        rejected.Status.Should().Be(ApprovalStatus.Rejected);
        rejected.ApprovalDate.Should().Be(rejectedAt);
    }

    #endregion

    #region Notification Creation Patterns

    [Fact]
    public void Notification_ForApprovalRequest_HasCorrectType()
    {
        var notification = new Notification
        {
            Title = "Approval Requested",
            Message = "Alice requested your approval for 'Publication review'",
            TargetNodePath = "org/doc",
            NotificationType = NotificationType.ApprovalRequired,
            CreatedBy = "alice"
        };

        notification.NotificationType.Should().Be(NotificationType.ApprovalRequired);
        notification.IsRead.Should().BeFalse();
    }

    [Fact]
    public void Notification_ForApprovalGiven_HasCorrectType()
    {
        var notification = new Notification
        {
            Title = "Approval Granted",
            Message = "Bob approved your request",
            TargetNodePath = "org/doc",
            NotificationType = NotificationType.ApprovalGiven,
            CreatedBy = "bob"
        };

        notification.NotificationType.Should().Be(NotificationType.ApprovalGiven);
    }

    [Fact]
    public void Notification_ForApprovalRejected_HasCorrectType()
    {
        var notification = new Notification
        {
            Title = "Approval Rejected",
            Message = "Bob rejected your request",
            TargetNodePath = "org/doc",
            NotificationType = NotificationType.ApprovalRejected,
            CreatedBy = "bob"
        };

        notification.NotificationType.Should().Be(NotificationType.ApprovalRejected);
    }

    #endregion

    #region ApprovalExtensions Configuration Tests

    [Fact]
    public void ApprovalExtensions_ApprovalPartition_HasCorrectValue()
    {
        ApprovalExtensions.ApprovalPartition.Should().Be("_Approval");
    }

    #endregion

    #region UserActivityLayoutAreas Tests

    [Fact]
    public void UserActivityLayoutAreas_ActivityArea_HasCorrectValue()
    {
        UserActivityLayoutAreas.ActivityArea.Should().Be("Activity");
    }

    #endregion

    #region WelcomeLayoutArea Tests

    [Fact]
    public void WelcomeLayoutArea_WelcomeArea_HasCorrectValue()
    {
        WelcomeLayoutArea.WelcomeArea.Should().Be("Welcome");
    }

    #endregion
}
