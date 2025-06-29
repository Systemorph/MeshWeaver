using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Todo.Domain;

public record TodoItem
{
    [Key][Browsable(false)]public string Id { get; init; } = Guid.NewGuid().AsString();
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    [Dimension<TodoCategory>] public string Category { get; init; } = "General";
    public DateTime? DueDate { get; init; }
    [Editable(false)]public TodoStatus Status { get; init; } = TodoStatus.Pending;
    [Editable(false)] public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    [Browsable(false)]public DateTime? UpdatedAt { get; init; }
}
