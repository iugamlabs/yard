using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace Yard.AspNetCore;

public sealed class YardUser : IdentityUser<Guid> {
    public string DisplayName { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public bool IsPlatformAdministrator { get; set; }
    public bool MustChangePassword { get; set; }
}

public sealed class PlatformSettings {
    public int Id { get; set; } = 1;
    public bool RegistrationEnabled { get; set; } = true;
    public bool BootstrapCompleted { get; set; }
}

public sealed class Organization {
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid OwnerId { get; set; }
    public bool Archived { get; set; }
    public long Version { get; set; }
}

public interface IOrganizationEntity {
    Guid OrganizationId { get; set; }
}

public sealed class OrganizationMember : IOrganizationEntity {
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
}

public sealed class OrganizationRole : IOrganizationEntity {
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string Permissions { get; set; } = "[]";
}

public sealed class MemberRole : IOrganizationEntity {
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
}

public sealed class OrganizationInvitation : IOrganizationEntity {
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Guid InviterId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Status { get; set; } = "pending";
}

public sealed class EmailChallenge {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Purpose { get; set; } = "";
    public string Email { get; set; } = "";
    public Guid? UserId { get; set; }
    public string Browser { get; set; } = "";
    public string Digest { get; set; } = "";
    public string Stamp { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public int Attempts { get; set; }
    public bool Sent { get; set; }
    public bool Consumed { get; set; }
}

public sealed class IdentityAudit {
    public long Id { get; set; }
    public Guid? ActorId { get; set; }
    public Guid? OrganizationId { get; set; }
    public string Action { get; set; } = "";
    public string Subject { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class IdentityLock {
    public int Id { get; set; } = 1;
    public long Version { get; set; }
}

public sealed class PendingExternal {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Provider { get; set; } = "github";
    public string ProviderKey { get; set; } = "";
    public string? Email { get; set; }
    public bool EmailVerified { get; set; }
    public string Browser { get; set; } = "";
    public Guid? BindingUserId { get; set; }
    public string? BindingStamp { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class OrganizationScope {
    public Guid? Id { get; internal set; }
    public bool Bypass { get; internal set; }
}

public class YardDbContext(DbContextOptions options, OrganizationScope scope)
    : IdentityDbContext<YardUser, IdentityRole<Guid>, Guid>(options) {
    public DbSet<PlatformSettings> PlatformSettings => Set<PlatformSettings>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<OrganizationRole> OrganizationRoles => Set<OrganizationRole>();
    public DbSet<MemberRole> MemberRoles => Set<MemberRole>();
    public DbSet<OrganizationInvitation> OrganizationInvitations => Set<OrganizationInvitation>();
    public DbSet<EmailChallenge> EmailChallenges => Set<EmailChallenge>();
    public DbSet<IdentityAudit> IdentityAudits => Set<IdentityAudit>();
    public DbSet<IdentityLock> IdentityLocks => Set<IdentityLock>();
    public DbSet<PendingExternal> PendingExternals => Set<PendingExternal>();

    protected override void OnModelCreating(ModelBuilder b) {
        base.OnModelCreating(b);
        b.UseOpenIddict();
        b.Entity<Organization>()
            .HasOne<YardUser>()
            .WithMany()
            .HasForeignKey(x => x.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Entity<OrganizationMember>()
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(x => x.OrganizationId);
        b.Entity<OrganizationMember>()
            .HasOne<YardUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        b.Entity<OrganizationRole>().HasAlternateKey(x => new { x.OrganizationId, x.Id });
        b.Entity<OrganizationRole>()
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(x => x.OrganizationId);
        b.Entity<MemberRole>()
            .HasOne<OrganizationRole>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.RoleId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id });
        b.Entity<MemberRole>()
            .HasOne<OrganizationMember>()
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.UserId });
        b.Entity<OrganizationInvitation>()
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(x => x.OrganizationId);
        b.Entity<OrganizationInvitation>()
            .HasOne<YardUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<YardUser>().HasIndex(x => x.NormalizedEmail).IsUnique();
        b.Entity<YardUser>().HasIndex(x => x.PhoneNumber).IsUnique();
        b.Entity<PlatformSettings>().HasData(new PlatformSettings());
        b.Entity<IdentityLock>().HasData(new IdentityLock());
        b.Entity<OrganizationMember>().HasKey(x => new { x.OrganizationId, x.UserId });
        b.Entity<MemberRole>()
            .HasKey(x => new {
                x.OrganizationId,
                x.UserId,
                x.RoleId,
            });
        b.Entity<OrganizationRole>().HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
        b.Entity<EmailChallenge>()
            .HasIndex(x => new {
                x.Email,
                x.Purpose,
                x.CreatedAt,
            });
        b.Entity<PendingExternal>().HasIndex(x => x.ProviderKey);
        b.Entity<IdentityUserLogin<Guid>>()
            .HasIndex(x => new { x.UserId, x.LoginProvider })
            .IsUnique();
        b.Entity<Organization>().Property(x => x.Version).IsConcurrencyToken();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder builder) {
        base.ConfigureConventions(builder);
        builder.Conventions.Add(_ => new OrganizationConvention(this));
    }

    public Guid? ActiveOrganizationId => scope.Id;
    public bool OrganizationBypass => scope.Bypass;

    public override Task<int> SaveChangesAsync(CancellationToken token = default) {
        foreach (
            var e in ChangeTracker
                .Entries<IOrganizationEntity>()
                .Where(x =>
                    x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                )
        )
            if (
                !scope.Bypass
                && (
                    scope.Id == null
                    || e.Entity.OrganizationId != scope.Id
                    || e.State != EntityState.Added
                        && (Guid)
                            e.Property(nameof(IOrganizationEntity.OrganizationId)).OriginalValue!
                            != scope.Id
                )
            )
                throw new YardHttpException(403, "organization_scope_required");
        return base.SaveChangesAsync(token);
    }

    public override int SaveChanges() {
        foreach (
            var e in ChangeTracker
                .Entries<IOrganizationEntity>()
                .Where(x =>
                    x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                )
        )
            if (
                !scope.Bypass
                && (
                    scope.Id == null
                    || e.Entity.OrganizationId != scope.Id
                    || e.State != EntityState.Added
                        && (Guid)
                            e.Property(nameof(IOrganizationEntity.OrganizationId)).OriginalValue!
                            != scope.Id
                )
            )
                throw new YardHttpException(403, "organization_scope_required");
        return base.SaveChanges();
    }
}

public sealed class YardHttpException(int status, string code) : Exception(code) {
    public int Status { get; } = status;
}

public interface IYardPermissionProvider {
    IEnumerable<string> Permissions { get; }
}

public sealed class StandardPermissions : IYardPermissionProvider {
    public IEnumerable<string> Permissions =>
        [
            "organization.read",
            "organization.update",
            "members.read",
            "members.manage",
            "roles.read",
            "roles.manage",
            "invitations.manage",
            "audit.read",
        ];
}

public sealed class YardSettings {
    public string CookieScope { get; internal set; } = "yard";
    public string Name { get; set; } = "Yard";
    public string Database { get; set; } = "postgresql";
    public string ConnectionString { get; set; } = "";
    public string Root { get; set; } = "";
    public string App { get; set; } = "api";
    public string PublicOrigin { get; set; } = "http://localhost:8080";
    public string VerificationKey { get; set; } = "";
    public bool Primary => App == "api";
}
