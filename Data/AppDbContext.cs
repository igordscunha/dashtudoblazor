using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DashTudo.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Dataset> Datasets => Set<Dataset>();
    public DbSet<DatasetContent> DatasetContents => Set<DatasetContent>();
    public DbSet<AiReport> AiReports => Set<AiReport>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<IdentityRole>().ToTable("Roles");
        builder.Entity<IdentityUserRole<string>>().ToTable("UserRoles");
        builder.Entity<IdentityUserClaim<string>>().ToTable("UserClaims");
        builder.Entity<IdentityUserLogin<string>>().ToTable("UserLogins");
        builder.Entity<IdentityUserToken<string>>().ToTable("UserTokens");
        builder.Entity<IdentityRoleClaim<string>>().ToTable("RoleClaims");

        builder.Entity<ApplicationUser>(e =>
        {
            e.ToTable("Users");
            e.Property(u => u.FirstName).HasMaxLength(100);
            e.Property(u => u.LastName).HasMaxLength(100);
            // O MySql.Data não suporta GetFieldValue<DateOnly>: a coluna "date" volta como DateTime e o cast falha.
            e.Property(u => u.BirthDate)
                .HasColumnType("date")
                .HasConversion(
                    d => d.HasValue ? d.Value.ToDateTime(TimeOnly.MinValue) : (DateTime?)null,
                    d => d.HasValue ? DateOnly.FromDateTime(d.Value) : null);
        });

        builder.Entity<Dataset>(e =>
        {
            e.Property(d => d.Name).HasMaxLength(200).IsRequired();
            e.Property(d => d.Description).HasMaxLength(1000);
            e.Property(d => d.OriginalFileName).HasMaxLength(260);
            e.Property(d => d.FileType).HasMaxLength(20);
            e.Property(d => d.ChartSettingsJson).HasColumnType("text");
            e.Property(d => d.ColumnLabelsJson).HasColumnType("text");
            e.HasIndex(d => new { d.UserId, d.CreatedAt });
            e.HasOne(d => d.User).WithMany(u => u.Datasets).HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.Content).WithOne(c => c.Dataset).HasForeignKey<DatasetContent>(c => c.DatasetId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DatasetContent>(e =>
        {
            e.HasKey(c => c.DatasetId);
            e.Property(c => c.ParsedData).HasColumnType("longblob");
            e.Property(c => c.OriginalFile).HasColumnType("longblob");
        });

        builder.Entity<AiReport>(e =>
        {
            e.Property(r => r.Question).HasMaxLength(2000);
            e.Property(r => r.ContentMarkdown).HasColumnType("mediumtext");
            e.Property(r => r.Model).HasMaxLength(64);
            e.HasIndex(r => new { r.DatasetId, r.CreatedAt });
            e.HasOne(r => r.Dataset).WithMany(d => d.Reports).HasForeignKey(r => r.DatasetId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
