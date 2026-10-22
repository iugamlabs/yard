using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace Yard.AspNetCore;

public sealed record LoginBody(string Identifier, string Password);

public sealed record RegisterBody(string Username, string Password);

public sealed record PasswordBody(string? CurrentPassword, string Password);

public sealed record ProfileBody(string DisplayName);

public sealed record UsernameBody(string Username, string? Password);

public sealed record EmailRequestBody(string Purpose, string? Email, string? Password);

public sealed record EmailVerifyBody(string ChallengeId, string Code);

public sealed record RecoveryBody(string ChallengeId, string Password);

public sealed record CreateUserBody(string Username, string Password, string? Email = null);

public sealed record UserStateBody(bool Enabled);

public sealed record AdministratorBody(bool Administrator, string? Password);

public sealed record PhoneBody(string? PhoneNumber, bool OwnershipConfirmed, string? Password);

public sealed record RegistrationBody(bool Enabled);

public sealed record SensitiveBody(string? Password);

public static class IdentityEndpoints {
    public static async Task SignIn(HttpContext h, YardUser u) =>
        await h
            .RequestServices.GetRequiredService<SignInManager<YardUser>>()
            .SignInWithClaimsAsync(
                u,
                false,
                [new Claim("yard:session", Guid.NewGuid().ToString("N"))]
            );

    public static void Map(WebApplication app) {
        var auth = app.MapGroup("/api/auth").AddEndpointFilter<BodyValidation>();
        auth.MapGet(
            "/csrf",
            (HttpContext h, IAntiforgery a) => {
                EmailVerification.Browser(h);
                return Results.Ok(new { token = a.GetAndStoreTokens(h).RequestToken });
            }
        );
        auth.MapGet(
            "/options",
            async (YardDbContext db, IConfiguration config) =>
                new {
                    registrationEnabled = await db
                        .PlatformSettings.Select(x => x.RegistrationEnabled)
                        .SingleAsync(),
                    githubEnabled = config.GetValue<bool>("Yard:GitHub:Enabled"),
                }
        );
        auth.MapGet(
                "/me",
                async (HttpContext h, IdentityOperations i) =>
                    Results.Ok(await i.View(await i.Current(h)))
            )
            .Produces<UserView>()
            .RequireAuthorization();
        auth.MapPost(
                "/register",
                async (RegisterBody body, HttpContext h, YardDbContext db, IdentityOperations i) => {
                    await using var tx = await db.Database.BeginTransactionAsync();
                    await i.Lock();
                    if (!await db.PlatformSettings.AnyAsync(x => x.RegistrationEnabled))
                        throw new YardHttpException(403, "registration_closed");
                    var u = await i.Create(body.Username, body.Password);
                    i.Audit("identity.registered", u.Id.ToString(), u.Id);
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                    return Results.Created("/api/auth/me", new { u.Id });
                }
            )
            .RequireRateLimiting("identity");
        auth.MapPost(
                "/login",
                async (
                    LoginBody body,
                    HttpContext h,
                    UserManager<YardUser> users,
                    SignInManager<YardUser> sign
                ) => {
                    var text = body.Identifier.Trim();
                    YardUser? u;
                    if (text.Contains('@'))
                        u = await users.Users.SingleOrDefaultAsync(x =>
                            x.NormalizedEmail == users.NormalizeEmail(text) && x.EmailConfirmed
                        );
                    else if (
                        text.StartsWith('+')
                        || System.Text.RegularExpressions.Regex.IsMatch(text, "^1[3-9][0-9]{9}$")
                    ) {
                        var phone = IdentityOperations.Phone(text);
                        u = await users.Users.SingleOrDefaultAsync(x =>
                            x.PhoneNumber == phone && x.PhoneNumberConfirmed
                        );
                    } else
                        u = await users.FindByNameAsync(text);
                    if (u == null || !u.IsEnabled) {
                        new PasswordHasher<YardUser>().VerifyHashedPassword(
                            new YardUser(),
                            DummyHash,
                            body.Password
                        );
                        throw new YardHttpException(400, "invalid_credentials");
                    }
                    var result = await sign.CheckPasswordSignInAsync(u, body.Password, true);
                    if (!result.Succeeded)
                        throw new YardHttpException(400, "invalid_credentials");
                    await SignIn(h, u);
                    return Results.Ok(new { u.MustChangePassword });
                }
            )
            .RequireRateLimiting("identity");
        auth.MapPost(
                "/logout",
                async (HttpContext h) => {
                    await h.SignOutAsync(IdentityConstants.ApplicationScheme);
                    return Results.NoContent();
                }
            )
            .RequireAuthorization();
        auth.MapPatch(
                "/profile",
                async (
                    ProfileBody b,
                    HttpContext h,
                    IdentityOperations i,
                    UserManager<YardUser> users
                ) => {
                    if (b.DisplayName.Trim().Length is < 1 or > 100)
                        throw new YardHttpException(400, "invalid_display_name");
                    var u = await i.Current(h);
                    u.DisplayName = b.DisplayName.Trim();
                    IdentityOperations.Check(await users.UpdateAsync(u));
                    return Results.Ok(await i.View(u));
                }
            )
            .RequireAuthorization();
        auth.MapPatch(
                "/username",
                async (
                    UsernameBody b,
                    HttpContext h,
                    IdentityOperations i,
                    YardDbContext db,
                    UserManager<YardUser> users
                ) => {
                    await using var tx = await db.Database.BeginTransactionAsync();
                    await i.Lock();
                    await i.VerifyPasswordOrProof(h, b.Password);
                    var u = await i.Current(h);
                    var name = IdentityOperations.Username(b.Username);
                    if (name != u.UserName) {
                        IdentityOperations.Check(await users.SetUserNameAsync(u, name));
                        await i.Revoke(u);
                        i.Audit("identity.username.changed", u.Id.ToString(), u.Id);
                        await db.SaveChangesAsync();
                    }
                    await tx.CommitAsync();
                    return Results.NoContent();
                }
            )
            .RequireAuthorization();
        auth.MapPost(
                "/change-password",
                async (
                    PasswordBody b,
                    HttpContext h,
                    IdentityOperations i,
                    UserManager<YardUser> users,
                    YardDbContext db
                ) => {
                    IdentityOperations.Password(b.Password);
                    await using var tx = await db.Database.BeginTransactionAsync();
                    await i.Lock();
                    var u = await i.Current(h);
                    await i.VerifyPasswordOrProof(h, b.CurrentPassword);
                    IdentityOperations.Check(
                        await users.HasPasswordAsync(u)
                            ? await users.ChangePasswordAsync(u, b.CurrentPassword!, b.Password)
                            : await users.AddPasswordAsync(u, b.Password)
                    );
                    u.MustChangePassword = false;
                    IdentityOperations.Check(await users.UpdateAsync(u));
                    await i.Revoke(u);
                    i.Audit("identity.password.changed", u.Id.ToString(), u.Id);
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                    return Results.NoContent();
                }
            )
            .RequireAuthorization();
        auth.MapPost(
                "/email/request",
                async (
                    EmailRequestBody b,
                    HttpContext h,
                    EmailVerification v,
                    IdentityOperations i,
                    CancellationToken ct
                ) => {
                    if (b.Purpose == "bind-email")
                        await i.VerifyPasswordOrProof(h, b.Password);
                    return Results.Ok(await v.Request(h, b.Purpose, b.Email, ct));
                }
            )
            .RequireRateLimiting("identity");
        auth.MapPost(
                "/email/verify",
                async (
                    EmailVerifyBody b,
                    HttpContext h,
                    EmailVerification v,
                    CancellationToken ct
                ) => {
                    var c = await v.Verify(h, b.ChallengeId, b.Code, ct);
                    return Results.Ok(
                        new { verified = true, signInAgain = c.Purpose == "bind-email" }
                    );
                }
            )
            .RequireRateLimiting("identity");
        auth.MapPost(
                "/recover-password",
                async (
                    RecoveryBody b,
                    HttpContext h,
                    IdentityOperations i,
                    YardDbContext db,
                    UserManager<YardUser> users
                ) => {
                    IdentityOperations.Password(b.Password);
                    await using var tx = await db.Database.BeginTransactionAsync();
                    await i.Lock();
                    var browser = EmailVerification.Browser(h);
                    var c = await db.EmailChallenges.SingleOrDefaultAsync(x =>
                        x.Id == b.ChallengeId
                    );
                    if (
                        c == null
                        || c.Purpose != "recover-password"
                        || c.Browser != browser
                        || c.UserId == null
                        || c.Consumed
                        || c.VerifiedAt == null
                        || c.VerifiedAt < DateTimeOffset.UtcNow.AddMinutes(-5)
                    )
                        throw new YardHttpException(400, "verification_invalid_or_expired");
                    var u = await users.FindByIdAsync(c.UserId.ToString()!);
                    if (
                        u == null
                        || !u.IsEnabled
                        || !u.EmailConfirmed
                        || u.Email != c.Email
                        || u.SecurityStamp != c.Stamp
                    )
                        throw new YardHttpException(400, "verification_invalid_or_expired");
                    var token = await users.GeneratePasswordResetTokenAsync(u);
                    IdentityOperations.Check(await users.ResetPasswordAsync(u, token, b.Password));
                    u.MustChangePassword = false;
                    IdentityOperations.Check(await users.UpdateAsync(u));
                    await i.Revoke(u);
                    c.Consumed = true;
                    i.Audit("identity.password.recovered", u.Id.ToString(), u.Id);
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                    return Results.NoContent();
                }
            )
            .RequireRateLimiting("identity");
        MapAdmin(app);
    }

    private static readonly string DummyHash = new PasswordHasher<YardUser>().HashPassword(
        new YardUser(),
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
    );

    private static void MapAdmin(WebApplication app) {
        var a = app.MapGroup("/api/admin")
            .RequireAuthorization()
            .AddEndpointFilter<BodyValidation>();
        a.MapGet(
            "/users",
            async (HttpContext h, IdentityOperations i, YardDbContext db, string? q, int? page) => {
                await i.Admin(h);
                var query = db.Users.AsNoTracking();
                if (!string.IsNullOrWhiteSpace(q))
                    query = query.Where(x =>
                        x.UserName!.Contains(q) || x.Email != null && x.Email.Contains(q)
                    );
                var total = await query.CountAsync();
                return Results.Ok(
                    new {
                        total,
                        items = await query
                            .OrderBy(x => x.UserName)
                            .Skip((Math.Max(page ?? 1, 1) - 1) * 20)
                            .Take(20)
                            .Select(x => new {
                                x.Id,
                                Username = x.UserName,
                                x.DisplayName,
                                x.Email,
                                x.EmailConfirmed,
                                x.PhoneNumber,
                                x.PhoneNumberConfirmed,
                                x.IsEnabled,
                                x.IsPlatformAdministrator,
                                x.MustChangePassword,
                            })
                            .ToListAsync(),
                    }
                );
            }
        );
        a.MapPost(
            "/users",
            async (CreateUserBody b, HttpContext h, IdentityOperations i, YardDbContext db) => {
                var actor = await i.Admin(h);
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                var u = await i.Create(b.Username, b.Password, temporary: true);
                i.Audit("admin.user.created", u.Id.ToString(), actor.Id);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Results.Ok(new { u.Id });
            }
        );
        a.MapPatch(
            "/users/{id:guid}/state",
            async (
                Guid id,
                UserStateBody b,
                HttpContext h,
                IdentityOperations i,
                YardDbContext db,
                UserManager<YardUser> users
            ) => {
                var actor = await i.Admin(h);
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                var u =
                    await users.FindByIdAsync(id.ToString())
                    ?? throw new YardHttpException(404, "user_not_found");
                if (!b.Enabled)
                    await i.ProtectLastAdmin(u);
                u.IsEnabled = b.Enabled;
                IdentityOperations.Check(await users.UpdateAsync(u));
                await i.Revoke(u);
                i.Audit("admin.user.state", id.ToString(), actor.Id);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Results.NoContent();
            }
        );
        a.MapPatch(
            "/users/{id:guid}/administrator",
            async (
                Guid id,
                AdministratorBody b,
                HttpContext h,
                IdentityOperations i,
                YardDbContext db,
                UserManager<YardUser> users
            ) => {
                var actor = await i.Admin(h);
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                await i.VerifyPasswordOrProof(h, b.Password);
                var u =
                    await users.FindByIdAsync(id.ToString())
                    ?? throw new YardHttpException(404, "user_not_found");
                if (!b.Administrator)
                    await i.ProtectLastAdmin(u);
                u.IsPlatformAdministrator = b.Administrator;
                IdentityOperations.Check(await users.UpdateAsync(u));
                await i.Revoke(u);
                i.Audit("admin.user.administrator", id + ":" + b.Administrator, actor.Id);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Results.NoContent();
            }
        );
        a.MapPost(
            "/users/{id:guid}/reset-password",
            async (
                Guid id,
                PasswordBody b,
                HttpContext h,
                IdentityOperations i,
                YardDbContext db,
                UserManager<YardUser> users
            ) => {
                var actor = await i.Admin(h);
                await i.VerifyPasswordOrProof(h, b.CurrentPassword);
                IdentityOperations.Password(b.Password);
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                var u =
                    await users.FindByIdAsync(id.ToString())
                    ?? throw new YardHttpException(404, "user_not_found");
                IdentityOperations.Check(
                    await users.ResetPasswordAsync(
                        u,
                        await users.GeneratePasswordResetTokenAsync(u),
                        b.Password
                    )
                );
                u.MustChangePassword = true;
                IdentityOperations.Check(await users.UpdateAsync(u));
                await i.Revoke(u);
                i.Audit("admin.user.password-reset", id.ToString(), actor.Id);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Results.NoContent();
            }
        );
        a.MapPost(
            "/users/{id:guid}/revoke-sessions",
            async (
                Guid id,
                HttpContext h,
                IdentityOperations i,
                YardDbContext db,
                UserManager<YardUser> users
            ) => {
                var actor = await i.Admin(h);
                var u =
                    await users.FindByIdAsync(id.ToString())
                    ?? throw new YardHttpException(404, "user_not_found");
                await i.Revoke(u);
                i.Audit("admin.user.sessions-revoked", id.ToString(), actor.Id);
                await db.SaveChangesAsync();
                return Results.NoContent();
            }
        );
        a.MapPatch(
            "/users/{id:guid}/phone",
            async (
                Guid id,
                PhoneBody b,
                HttpContext h,
                IdentityOperations i,
                YardDbContext db,
                UserManager<YardUser> users
            ) => {
                var actor = await i.Admin(h);
                await i.VerifyPasswordOrProof(h, b.Password);
                var phone = IdentityOperations.Phone(b.PhoneNumber);
                if (phone != null && !b.OwnershipConfirmed)
                    throw new YardHttpException(400, "phone_ownership_confirmation_required");
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                var u =
                    await users.FindByIdAsync(id.ToString())
                    ?? throw new YardHttpException(404, "user_not_found");
                if (u.PhoneNumber != phone) {
                    u.PhoneNumber = phone;
                    u.PhoneNumberConfirmed = phone != null;
                    IdentityOperations.Check(await users.UpdateAsync(u));
                    await i.Revoke(u);
                    i.Audit("admin.user.phone-verified", id.ToString(), actor.Id);
                    await db.SaveChangesAsync();
                }
                await tx.CommitAsync();
                return Results.NoContent();
            }
        );
        a.MapGet(
            "/registration",
            async (HttpContext h, IdentityOperations i, YardDbContext db) => {
                await i.Admin(h);
                return Results.Ok(
                    new {
                        enabled = await db
                            .PlatformSettings.Select(x => x.RegistrationEnabled)
                            .SingleAsync(),
                    }
                );
            }
        );
        a.MapPatch(
            "/registration",
            async (RegistrationBody b, HttpContext h, IdentityOperations i, YardDbContext db) => {
                var actor = await i.Admin(h);
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                var s = await db.PlatformSettings.SingleAsync();
                s.RegistrationEnabled = b.Enabled;
                i.Audit("admin.registration.updated", b.Enabled.ToString(), actor.Id);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Results.NoContent();
            }
        );
        a.MapGet(
            "/audit",
            async (HttpContext h, IdentityOperations i, YardDbContext db, int? page) => {
                await i.Admin(h);
                return Results.Ok(
                    await db
                        .IdentityAudits.OrderByDescending(x => x.Id)
                        .Skip((Math.Max(1, page ?? 1) - 1) * 50)
                        .Take(50)
                        .ToListAsync()
                );
            }
        );
        a.MapPatch(
            "/organizations/{id:guid}/archive",
            async (Guid id, ArchiveBody b, HttpContext h, IdentityOperations i, YardDbContext db) => {
                var actor = await i.Admin(h);
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                var o =
                    await db.Organizations.SingleOrDefaultAsync(x => x.Id == id)
                    ?? throw new YardHttpException(404, "organization_not_found");
                o.Archived = b.Archived;
                o.Version++;
                i.Audit("admin.organization.archive", b.Archived.ToString(), actor.Id, id);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Results.NoContent();
            }
        );
        a.MapGet(
            "/organizations",
            async (HttpContext h, IdentityOperations i, YardDbContext db) => {
                await i.Admin(h);
                return Results.Ok(
                    await db.Organizations.AsNoTracking().OrderBy(x => x.Name).ToListAsync()
                );
            }
        );
    }
}
