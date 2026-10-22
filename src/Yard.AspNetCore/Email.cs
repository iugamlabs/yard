using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Yard.AspNetCore;

public sealed class SmtpOptions {
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "Yard";
    public string Tls { get; set; } = "StartTls";
}

public interface IEmailSender {
    Task SendAsync(string recipient, string subject, string text, CancellationToken ct = default);
}

public interface ISmtpClientFactory {
    SmtpClient Create();
}

public sealed class SmtpClientFactory : ISmtpClientFactory {
    public SmtpClient Create() => new();
}

public sealed class SmtpEmailSender(IOptions<SmtpOptions> options, ISmtpClientFactory factory)
    : IEmailSender {
    public async Task SendAsync(
        string recipient,
        string subject,
        string text,
        CancellationToken ct = default
    ) {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Host) || string.IsNullOrWhiteSpace(o.FromAddress))
            throw new YardHttpException(503, "smtp_not_configured");
        var tls = o.Tls switch {
            "StartTls" => SecureSocketOptions.StartTls,
            "SslOnConnect" => SecureSocketOptions.SslOnConnect,
            "None" when o.Host is "localhost" or "127.0.0.1" => SecureSocketOptions.None,
            _ => throw new YardHttpException(503, "invalid_smtp_tls"),
        };
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = text };
        using var smtp = factory.Create();
        smtp.Timeout = 10000;
        await smtp.ConnectAsync(o.Host, o.Port, tls, ct);
        if (o.Username.Length > 0)
            await smtp.AuthenticateAsync(o.Username, o.Password, ct);
        await smtp.SendAsync(message, ct);
        await smtp.DisconnectAsync(true, ct);
    }
}

public sealed record CapturedMail(
    string Id,
    string Recipient,
    string Subject,
    string Text,
    DateTimeOffset At
);

public sealed class DevelopmentEmailSender(YardSettings settings) : IEmailSender {
    public Task SendAsync(
        string recipient,
        string subject,
        string text,
        CancellationToken ct = default
    ) {
        var dir = Path.Combine(settings.Root, ".yard", "mail");
        Directory.CreateDirectory(dir);
        var mail = new CapturedMail(
            Guid.NewGuid().ToString("N"),
            recipient,
            subject,
            text,
            DateTimeOffset.UtcNow
        );
        var path = Path.Combine(dir, mail.Id + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(mail));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return Task.CompletedTask;
    }
}

public sealed class EmailVerification(
    YardDbContext db,
    IdentityOperations identity,
    UserManager<YardUser> users,
    IEmailSender mail,
    YardSettings settings
) {
    public static string Browser(HttpContext h) {
        var name =
            "yard-browser-" + h.RequestServices.GetRequiredService<YardSettings>().CookieScope;
        if (!h.Request.Cookies.TryGetValue(name, out var value) || value.Length != 64) {
            value = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            h.Response.Cookies.Append(
                name,
                value,
                new CookieOptions {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = h.Request.IsHttps,
                    MaxAge = TimeSpan.FromHours(8),
                    Path = "/",
                }
            );
        }
        return value + ":" + (h.User.FindFirstValue("yard:session") ?? "");
    }

    private string Digest(EmailChallenge c, string code) =>
        Convert.ToHexStringLower(
            HMACSHA256.HashData(
                Convert.FromBase64String(settings.VerificationKey),
                Encoding.UTF8.GetBytes($"{c.Id}|{c.Purpose}|{c.UserId}|{c.Email}|{code}")
            )
        );

    public async Task<object> Request(
        HttpContext h,
        string purpose,
        string? address,
        CancellationToken ct
    ) {
        if (
            purpose is not ("bind-email" or "recover-password" or "reauth" or "github-registration")
        )
            throw new YardHttpException(400, "invalid_verification_purpose");
        var browser = Browser(h);
        YardUser? user = null;
        if (purpose is "bind-email" or "reauth")
            user = await identity.Current(h);
        var email = IdentityOperations.Email(
            purpose == "reauth" ? user?.Email ?? "" : address ?? ""
        );
        if (purpose == "reauth" && user?.EmailConfirmed != true)
            throw new YardHttpException(400, "verified_email_required");
        if (purpose == "recover-password")
            user = await users.FindByEmailAsync(email);
        if (
            purpose == "github-registration"
            && !await db.PendingExternals.AnyAsync(
                x => x.Browser == browser && x.BindingUserId == null,
                ct
            )
        )
            throw new YardHttpException(400, "external_registration_missing");
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await identity.Lock(ct);
        var recent = await db
            .EmailChallenges.Where(x => x.Email == email && x.Purpose == purpose)
            .ToListAsync(ct);
        if (recent.Any(x => x.CreatedAt > now.AddSeconds(-60)))
            throw new YardHttpException(429, "verification_resend_too_soon");
        if (recent.Count(x => x.CreatedAt > now.AddHours(-1)) >= 10)
            throw new YardHttpException(429, "verification_send_limit");
        foreach (var old in recent.Where(x => !x.Consumed))
            old.Consumed = true;
        var code = RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6");
        var c = new EmailChallenge {
            Purpose = purpose,
            Email = email,
            UserId = user?.Id,
            Browser = browser,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(5),
            Stamp = user?.SecurityStamp ?? "",
        };
        c.Digest = Digest(c, code);
        db.EmailChallenges.Add(c);
        await db.SaveChangesAsync(ct);
        // Unknown recovery addresses have the same response and do not disclose account existence.
        if (purpose != "recover-password" || user is { EmailConfirmed: true, IsEnabled: true }) {
            try {
                await mail.SendAsync(
                    email,
                    $"[{settings.Name}] 邮箱验证码",
                    $"您的验证码：{code}\n五分钟内有效，仅用于本次 {purpose} 操作。\n如果您没有发起操作，请忽略此邮件。\n{settings.PublicOrigin}",
                    ct
                );
            }
            catch (OperationCanceledException) {
                throw;
            }
            catch (Exception) {
                throw new YardHttpException(503, "email_delivery_failed");
            }
        }
        c.Sent = true;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new {
            ChallengeId = c.Id,
            ExpiresIn = 300,
            RetryAfter = 60,
        };
    }

    public async Task<EmailChallenge> Verify(
        HttpContext h,
        string id,
        string code,
        CancellationToken ct = default
    ) {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await identity.Lock(ct);
        var c = await db.EmailChallenges.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (
            c == null
            || c.Browser != Browser(h)
            || c.Consumed
            || !c.Sent
            || c.VerifiedAt != null
            || c.ExpiresAt <= DateTimeOffset.UtcNow
            || c.Attempts >= 5
        )
            throw new YardHttpException(400, "verification_invalid_or_expired");
        c.Attempts++;
        var good =
            code.Length == 6
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(c.Digest),
                Encoding.UTF8.GetBytes(Digest(c, code))
            );
        if (!good) {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw new YardHttpException(400, "verification_invalid_or_expired");
        }
        c.VerifiedAt = DateTimeOffset.UtcNow;
        if (c.Purpose == "bind-email") {
            var u = await identity.Current(h);
            if (u.Id != c.UserId || u.SecurityStamp != c.Stamp)
                throw new YardHttpException(400, "verification_invalid_or_expired");
            var existing = await users.FindByEmailAsync(c.Email);
            if (existing != null && existing.Id != u.Id)
                throw new YardHttpException(409, "email_already_used");
            IdentityOperations.Check(await users.SetEmailAsync(u, c.Email));
            u.EmailConfirmed = true;
            IdentityOperations.Check(await users.UpdateAsync(u));
            await identity.Revoke(u);
            c.Consumed = true;
            identity.Audit("identity.email.changed", u.Id.ToString(), u.Id);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return c;
    }
}
