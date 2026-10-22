using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yard.AspNetCore;

public sealed class IdentityOperations(YardDbContext db, UserManager<YardUser> users) {
    public static Guid Actor(HttpContext h) =>
        Guid.TryParse(h.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new YardHttpException(401, "authentication_required");

    public async Task<YardUser> Current(HttpContext h) =>
        await users.FindByIdAsync(Actor(h).ToString())
        ?? throw new YardHttpException(401, "authentication_required");

    public async Task<YardUser> Admin(HttpContext h) {
        var u = await Current(h);
        if (!u.IsPlatformAdministrator)
            throw new YardHttpException(403, "platform_administrator_required");
        return u;
    }

    public static void Check(IdentityResult result) {
        if (!result.Succeeded)
            throw new YardHttpException(400, string.Join("; ", result.Errors.Select(x => x.Code)));
    }

    public static string Username(string value) {
        value = value.Trim();
        if (!Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9_.-]{2,31}$"))
            throw new YardHttpException(400, "invalid_username");
        return value;
    }

    public static string Email(string value) {
        value = value.Trim();
        if (
            value.Length > 254
            || !System.Net.Mail.MailAddress.TryCreate(value, out var mail)
            || mail.Address != value
        )
            throw new YardHttpException(400, "invalid_email");
        return value.ToLowerInvariant();
    }

    public static string? Phone(string? value) {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim().Replace(" ", "").Replace("-", "");
        if (Regex.IsMatch(value, "^1[3-9][0-9]{9}$"))
            value = "+86" + value;
        if (!Regex.IsMatch(value, "^\\+[1-9][0-9]{7,14}$"))
            throw new YardHttpException(400, "invalid_phone");
        return value;
    }

    public static void Password(string value) {
        if (value.Length < 12 || value.Length > 128)
            throw new YardHttpException(400, "password_requires_12_to_128_characters");
    }

    public async Task Lock(CancellationToken ct = default) =>
        _ = await db
            .IdentityLocks.Where(x => x.Id == 1)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Version, p => p.Version + 1), ct);

    public void Audit(
        string action,
        string subject,
        Guid? actor = null,
        Guid? organization = null
    ) =>
        db.IdentityAudits.Add(
            new IdentityAudit {
                ActorId = actor,
                Action = action,
                Subject = subject,
                OrganizationId = organization,
            }
        );

    public async Task<YardUser> Create(
        string username,
        string? password,
        bool admin = false,
        bool temporary = false
    ) {
        username = Username(username);
        if (password != null)
            Password(password);
        var u = new YardUser {
            Id = Guid.NewGuid(),
            UserName = username,
            DisplayName = username,
            IsPlatformAdministrator = admin,
            MustChangePassword = temporary,
            SecurityStamp = Guid.NewGuid().ToString("N"),
        };
        Check(password == null ? await users.CreateAsync(u) : await users.CreateAsync(u, password));
        return u;
    }

    public async Task Revoke(YardUser u) {
        Check(await users.UpdateSecurityStampAsync(u));
    }

    public async Task<UserView> View(YardUser u) =>
        new(
            u.Id,
            u.UserName!,
            u.DisplayName,
            u.Email,
            u.EmailConfirmed,
            u.PhoneNumber,
            u.PhoneNumberConfirmed,
            u.IsEnabled,
            u.IsPlatformAdministrator,
            u.MustChangePassword,
            await users.HasPasswordAsync(u),
            (await users.GetLoginsAsync(u)).Select(x => x.LoginProvider).ToArray()
        );

    public async Task VerifyPasswordOrProof(HttpContext h, string? password) {
        if (db.Database.CurrentTransaction == null) {
            await using var tx = await db.Database.BeginTransactionAsync();
            await Lock();
            await VerifyPasswordOrProof(h, password);
            await tx.CommitAsync();
            return;
        }
        var u = await Current(h);
        if (await users.HasPasswordAsync(u)) {
            if (password == null || !await users.CheckPasswordAsync(u, password))
                throw new YardHttpException(400, "current_password_invalid");
        } else {
            var browser = EmailVerification.Browser(h);
            var now = DateTimeOffset.UtcNow;
            var proofs = await db
                .EmailChallenges.Where(x =>
                    x.UserId == u.Id
                    && x.Purpose == "reauth"
                    && x.Browser == browser
                    && x.VerifiedAt != null
                    && !x.Consumed
                )
                .ToListAsync();
            var proof = proofs.SingleOrDefault(x =>
                x.VerifiedAt > now.AddMinutes(-5) && x.Stamp == u.SecurityStamp
            );
            if (proof == null)
                throw new YardHttpException(400, "email_verification_required");
            proof.Consumed = true;
            await db.SaveChangesAsync();
        }
    }

    public async Task ProtectLastAdmin(YardUser u) {
        if (
            u.IsPlatformAdministrator
            && u.IsEnabled
            && await db.Users.CountAsync(x => x.IsPlatformAdministrator && x.IsEnabled) <= 1
        )
            throw new YardHttpException(409, "last_administrator_protected");
    }
}

public sealed record UserView(
    Guid Id,
    string Username,
    string DisplayName,
    string? Email,
    bool EmailConfirmed,
    string? PhoneNumber,
    bool PhoneNumberConfirmed,
    bool IsEnabled,
    bool IsPlatformAdministrator,
    bool MustChangePassword,
    bool HasPassword,
    string[] ExternalLogins
);
