namespace MeshWeaver.Todo;

public record TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Category { get; set; } = "General";
    public DateTime? DueDate { get; set; }
    public TodoStatus Status { get; set; } = TodoStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
