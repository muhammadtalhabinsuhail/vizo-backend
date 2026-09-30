using Microsoft.EntityFrameworkCore;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- the sidebar's "new" markers (migration 42).
/// Its own file so AppDbContext.cs (Talha's) is not edited. The mapping is all
/// data annotations on <see cref="UserSeenMarker"/>.
/// </summary>
public partial class AppDbContext
{
    public virtual DbSet<UserSeenMarker> UserSeenMarkers { get; set; } = null!;
}
