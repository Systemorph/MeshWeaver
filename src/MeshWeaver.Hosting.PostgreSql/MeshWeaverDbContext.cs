using MeshWeaver.Articles;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Entity Framework Core DbContext for MeshWeaver PostgreSQL implementation.
/// Manages Article, MeshNode entities, and ASP.NET Core antiforgery keys.
/// </summary>
public class MeshWeaverDbContext(DbContextOptions<MeshWeaverDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    /// <summary>
    /// Collection of articles stored in the database.
    /// </summary>
    public DbSet<Article> Articles { get; set; } = null!;

    /// <summary>
    /// Collection of authors for articles.
    /// </summary>
    public DbSet<Author> Authors { get; set; } = null!;

    /// <summary>
    /// Collection of mesh nodes for network configuration.
    /// </summary>
    public DbSet<MeshNode> MeshNodes { get; set; } = null!;

    /// <summary>
    /// Data protection keys used by ASP.NET Core for antiforgery tokens.
    /// This implementation complies with IDataProtectionKeyContext interface.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    public DbSet<MessageLog> Messages { get; set; } = null!;
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure Article entity
        modelBuilder.Entity<Article>(entity =>
        {
            entity.HasKey(e => e.Url);
            entity.Property(e => e.Collection).IsRequired();
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.Title);
            entity.Property(e => e.Content);
            entity.Property(e => e.PrerenderedHtml);
            entity.Property(e => e.Path).IsRequired();
            entity.Property(e => e.PrerenderedHtml);
            entity.Property(e => e.LastUpdated);
            entity.Property(e => e.Status);
            entity.Property(e => e.VideoTranscript);
            entity.Property(e => e.VideoDuration);
            entity.Property(e => e.VideoUrl);
            entity.Property(e => e.VideoTitle);
            entity.Property(e => e.VideoDescription);

            // Handle collections (convert to JSON)
            entity.Property(e => e.Authors)
                .HasConversion(
                    v => string.Join(',', v),
                    v => v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList());

            entity.Property(e => e.Tags)
                .HasConversion(
                    v => string.Join(',', v),
                    v => v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList());

            // Use PostgreSQL specific features for more complex types
            entity.Property(e => e.VectorRepresentation).HasColumnType("real[]");

            // Store complex data as JSON
            entity.Property(e => e.StatusHistory).HasColumnType("jsonb");
            entity.Property(e => e.AuthorDetails).HasColumnType("jsonb");
            entity.Property(e => e.CodeSubmissions).HasColumnType("jsonb");
            entity.Property(e => e.Icon).HasColumnType("jsonb");
        });

        // Configure Author entity
        modelBuilder.Entity<Author>(entity =>
        {
            // Create a composite key from FirstName and LastName
            entity.HasKey(e => new { e.FirstName, e.LastName });

            entity.Property(e => e.FirstName).IsRequired();
            entity.Property(e => e.LastName).IsRequired();
            entity.Property(e => e.MiddleName);
            entity.Property(e => e.ImageUrl);

            // Since Author is a C# record, ensure value comparison works correctly
            entity.HasIndex(e => new { e.FirstName, e.LastName }).IsUnique();
        });

        // Configure MeshNode entity (using the existing MeshNode type from the library)
        modelBuilder.Entity<MeshNode>(entity =>
        {
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).IsRequired();
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.AddressType).IsRequired();
            entity.Property(e => e.AddressId);
            entity.Property(e => e.ThumbNail);
            entity.Property(e => e.StreamProvider);
            entity.Property(e => e.Namespace);
            entity.Property(e => e.AssemblyLocation);
            entity.Property(e => e.StartupScript);
            entity.Property(e => e.RoutingType);
            entity.Property(e => e.InstantiationType);

            // Ignore the HubConfiguration as it's not serializable
            entity.Ignore(e => e.HubConfiguration);
        });

        // Configure DataProtectionKey entity
        modelBuilder.Entity<DataProtectionKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FriendlyName);
            entity.Property(e => e.Xml).IsRequired();
        });

        // Configure MessageLog entity
        modelBuilder.Entity<MessageLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id)
                  .UseIdentityAlwaysColumn(); // Auto-generate the Id
            entity.Property(e => e.Timestamp);
            entity.Property(e => e.Level).IsRequired();
            entity.Property(e => e.Properties).HasColumnType("jsonb");
            entity.Property(e => e.Message);
            entity.Property(e => e.Exception);
        });
    }
}
