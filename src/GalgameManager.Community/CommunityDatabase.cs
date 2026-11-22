using GalgameManager.Application.Community;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GalgameManager.Community;

public sealed class CommunityUser : IdentityUser
{
    public string Nickname { get; set; } = "";
}

public sealed class CommunitySession
{
    public required string TokenHash { get; init; }
    public required string UserId { get; init; }
    public CommunityUser User { get; set; } = null!;
    public DateTime ExpiresUtc { get; init; }
}

public sealed class Topic
{
    public long Id { get; set; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required string AuthorId { get; init; }
    public CommunityUser Author { get; set; } = null!;
    public DateTime CreatedUtc { get; init; }
    public ICollection<Reply> Replies { get; } = new List<Reply>();
}

public sealed class Reply
{
    public long Id { get; set; }
    public long TopicId { get; init; }
    public required string Body { get; init; }
    public required string AuthorId { get; init; }
    public CommunityUser Author { get; set; } = null!;
    public DateTime CreatedUtc { get; init; }
}

public sealed class CommunityDatabase(DbContextOptions<CommunityDatabase> options)
    : IdentityUserContext<CommunityUser>(options)
{
    public DbSet<CommunitySession> Sessions => Set<CommunitySession>();
    public DbSet<Topic> Topics => Set<Topic>();
    public DbSet<Reply> Replies => Set<Reply>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<CommunityUser>(user =>
        {
            user.Property(item => item.Id).HasMaxLength(36);
            user.Property(item => item.Nickname).IsRequired().HasMaxLength(CommunityLimits.Nickname);
            user.HasIndex(item => item.NormalizedEmail).IsUnique();
        });
        builder.Entity<CommunitySession>(session =>
        {
            session.HasKey(item => item.TokenHash);
            session.Property(item => item.TokenHash).HasMaxLength(64).IsFixedLength();
            session.HasIndex(item => item.ExpiresUtc);
            session.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId);
        });
        builder.Entity<Topic>(topic =>
        {
            topic.Property(item => item.Title).IsRequired().HasMaxLength(CommunityLimits.TopicTitle);
            topic.Property(item => item.Body).IsRequired();
            topic.HasIndex(item => new { item.CreatedUtc, item.Id });
            topic.HasOne(item => item.Author).WithMany().HasForeignKey(item => item.AuthorId).OnDelete(DeleteBehavior.Restrict);
            topic.HasMany(item => item.Replies).WithOne().HasForeignKey(item => item.TopicId);
        });
        builder.Entity<Reply>(reply =>
        {
            reply.Property(item => item.Body).IsRequired();
            reply.HasIndex(item => new { item.TopicId, item.Id });
            reply.HasOne(item => item.Author).WithMany().HasForeignKey(item => item.AuthorId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
