namespace MeshWeaver.CreativeCloud.Domain;

/// <summary>
/// Represents the status of content items in the content portal
/// </summary>
public enum ContentStatus
{
    /// <summary>
    /// Content is in draft state
    /// </summary>
    Draft = 0,

    /// <summary>
    /// Content is being reviewed
    /// </summary>
    InReview = 1,

    /// <summary>
    /// Content has been approved
    /// </summary>
    Approved = 2,

    /// <summary>
    /// Content is scheduled for publication
    /// </summary>
    Scheduled = 3,

    /// <summary>
    /// Content has been published
    /// </summary>
    Published = 4,

    /// <summary>
    /// Content has been archived
    /// </summary>
    Archived = 5
}
