using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Client;
using OpenIddict.Client.AspNetCore;
using OpenIddict.Client.WebIntegration;

namespace Yard.AspNetCore;

public sealed record CompleteGitHubBody(
    string Username,
    string Email,
    string? Password,
    string? ChallengeId
);

public static class GitHubIdentity {
    public static void ConfigureTestProvider(
        OpenIddictClientBuilder builder,
        IConfiguration c,
        IHostEnvironment env
    ) {
        builder.UseWebProviders();
        var registration = new OpenIddictClientRegistration {
            RegistrationId = "github",
            ProviderName = "github",
            ProviderType = OpenIddictClientWebIntegrationConstants.ProviderTypes.GitHub,
            ProviderSettings = new OpenIddictClientWebIntegrationSettings.GitHub(),
            Issuer = new Uri("https://github.com/"),
            ClientId = c["Yard:GitHub:ClientId"],
            ClientSecret = c["Yard:GitHub:ClientSecret"],
            RedirectUri = new Uri(
                c["Yard:PublicOrigin"]!.TrimEnd('/') + "/api/auth/external/github/callback"
            ),
        };
        registration.Scopes.UnionWith(["read:user", "user:email"]);
        registration.CodeChallengeMethods.Add("S256");
        if (c["Yard:GitHub:DevelopmentServer"] is { Length: > 0 } address) {
            if (
                !env.IsDevelopment()
                || !Uri.TryCreate(address, UriKind.Absolute, out var server)
                || !server.IsLoopback
                || server.Scheme != "http"
            )
                throw new InvalidOperationException(
                    "GitHub development server requires Development and loopback HTTP."
                );
            registration.Configuration = new OpenIddictConfiguration {
                Issuer = new Uri("https://github.com/"),
                AuthorizationEndpoint = new Uri(server, "/login/oauth/authorize"),
                TokenEndpoint = new Uri(server, "/login/oauth/access_token"),
                UserInfoEndpoint = new Uri(server, "/user"),
                GrantTypesSupported = { "authorization_code" },
                ResponseTypesSupported = { "code" },
                CodeChallengeMethodsSupported = { "S256" },
                TokenEndpointAuthMethodsSupported = { "client_secret_post" },
            };
        }
        builder.AddRegistration(registration);
    }

    private static void Enabled(IConfiguration c) {
        if (!c.GetValue<bool>("Yard:GitHub:Enabled"))
            throw new YardHttpException(404, "github_disabled");
    }

    public static void Map(WebApplication app) {
        var auth = app.MapGroup("/api/auth/external").AddEndpointFilter<BodyValidation>();
        auth.MapPost(
                "/github/login",
                (HttpContext h, IConfiguration c) => {
                    Enabled(c);
                    if (h.User.Identity?.IsAuthenticated == true)
                        throw new YardHttpException(409, "already_authenticated");
                    return Start(h, null);
                }
            )
            .RequireRateLimiting("identity");
        auth.MapPost(
                "/github/link",
                async (HttpContext h, IConfiguration c, IdentityOperations i) => {
                    Enabled(c);
                    var form = await h.Request.ReadFormAsync();
                    await i.VerifyPasswordOrProof(h, form["password"]);
                    return Start(h, await i.Current(h));
                }
            )
            .RequireAuthorization();
        auth.MapGet(
            "/github/callback",
            async (
                HttpContext h,
                IConfiguration c,
                YardDbContext db,
                IdentityOperations i,
                UserManager<YardUser> users,
                IHttpClientFactory clients
            ) => {
                try {
                    Enabled(c);
                    var result = await h.AuthenticateAsync(
                        OpenIddictClientAspNetCoreDefaults.AuthenticationScheme
                    );
                    if (!result.Succeeded || result.Principal == null || result.Properties == null)
                        throw new YardHttpException(400, "github_authorization_failed");
                    var key = result.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
                    if (!ulong.TryParse(key, out var id) || id == 0)
                        throw new YardHttpException(400, "github_authorization_failed");
                    var browser = EmailVerification.Browser(h);
                    if (
                        result
                            .Properties.Items.ToDictionary(x => x.Key, x => x.Value)
                            .GetValueOrDefault("yard:browser") != browser
                    )
                        throw new YardHttpException(400, "github_browser_changed");
                    await using var tx = await db.Database.BeginTransactionAsync();
                    await i.Lock();
                    if (
                        result
                            .Properties.Items.ToDictionary(x => x.Key, x => x.Value)
                            .GetValueOrDefault("yard:purpose") == "link"
                    ) {
                        var u = await i.Current(h);
                        if (
                            result
                                .Properties.Items.ToDictionary(x => x.Key, x => x.Value)
                                .GetValueOrDefault("yard:user") != u.Id.ToString()
                            || result
                                .Properties.Items.ToDictionary(x => x.Key, x => x.Value)
                                .GetValueOrDefault("yard:stamp") != u.SecurityStamp
                        )
                            throw new YardHttpException(400, "github_session_changed");
                        if (
                            (await users.GetLoginsAsync(u)).Any(x => x.LoginProvider == "github")
                            || await users.FindByLoginAsync("github", key!) != null
                        )
                            throw new YardHttpException(409, "github_already_linked");
                        IdentityOperations.Check(
                            await users.AddLoginAsync(
                                u,
                                new UserLoginInfo("github", key!, "GitHub")
                            )
                        );
                        await i.Revoke(u);
                        i.Audit("identity.github.linked", u.Id.ToString(), u.Id);
                        await db.SaveChangesAsync();
                        await tx.CommitAsync();
                        return Results.Redirect("/login?notice=github-linked");
                    }
                    if (h.User.Identity?.IsAuthenticated == true)
                        throw new YardHttpException(409, "github_session_changed");
                    var linked = await users.FindByLoginAsync("github", key!);
                    if (linked != null) {
                        if (!linked.IsEnabled)
                            throw new YardHttpException(400, "invalid_credentials");
                        await tx.CommitAsync();
                        await IdentityEndpoints.SignIn(h, linked);
                        return Results.Redirect("/");
                    }
                    if (!await db.PlatformSettings.AnyAsync(x => x.RegistrationEnabled))
                        throw new YardHttpException(403, "registration_closed");
                    var token =
                        result.Properties.GetTokenValue(
                            OpenIddictClientAspNetCoreConstants.Tokens.BackchannelAccessToken
                        ) ?? throw new YardHttpException(400, "github_authorization_failed");
                    var baseUri = c["Yard:GitHub:DevelopmentServer"] ?? "https://api.github.com/";
                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        new Uri(new Uri(baseUri), "/user/emails")
                    );
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.UserAgent.ParseAdd("Iugam.Yard/0.1.0");
                    request.Headers.Accept.ParseAdd("application/vnd.github+json");
                    request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                    using var response = await clients
                        .CreateClient()
                        .SendAsync(request, h.RequestAborted);
                    response.EnsureSuccessStatusCode();
                    using var json = JsonDocument.Parse(
                        await response.Content.ReadAsStringAsync(h.RequestAborted)
                    );
                    var email = json
                        .RootElement.EnumerateArray()
                        .Where(x =>
                            x.GetProperty("primary").GetBoolean()
                            && x.GetProperty("verified").GetBoolean()
                        )
                        .Select(x => x.GetProperty("email").GetString())
                        .FirstOrDefault(x =>
                            x != null
                            && !x.EndsWith(
                                "@users.noreply.github.com",
                                StringComparison.OrdinalIgnoreCase
                            )
                        );
                    db.PendingExternals.RemoveRange(
                        await db.PendingExternals.Where(x => x.Browser == browser).ToListAsync()
                    );
                    db.PendingExternals.Add(
                        new PendingExternal {
                            ProviderKey = key!,
                            Email = email,
                            EmailVerified = email != null,
                            Browser = browser,
                            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                        }
                    );
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                    return Results.Redirect("/github-register");
                }
                catch (Exception e) when (!h.RequestAborted.IsCancellationRequested) {
                    var error = e is YardHttpException ? e.Message : "github_authorization_failed";
                    return Results.Redirect("/login?error=" + Uri.EscapeDataString(error));
                }
            }
        );
        auth.MapGet(
            "/registration",
            async (HttpContext h, IConfiguration c, YardDbContext db) => {
                Enabled(c);
                var browser = EmailVerification.Browser(h);
                var p =
                    await db.PendingExternals.SingleOrDefaultAsync(x => x.Browser == browser)
                    ?? throw new YardHttpException(400, "github_registration_missing");
                if (p.ExpiresAt < DateTimeOffset.UtcNow)
                    throw new YardHttpException(400, "github_registration_expired");
                return Results.Ok(
                    new {
                        p.Email,
                        p.EmailVerified,
                        p.ExpiresAt,
                    }
                );
            }
        );
        auth.MapPost(
                "/registration",
                async (
                    CompleteGitHubBody b,
                    HttpContext h,
                    IConfiguration c,
                    YardDbContext db,
                    IdentityOperations i,
                    UserManager<YardUser> users
                ) => {
                    Enabled(c);
                    if (h.User.Identity?.IsAuthenticated == true)
                        throw new YardHttpException(409, "already_authenticated");
                    var browser = EmailVerification.Browser(h);
                    var email = IdentityOperations.Email(b.Email);
                    await using var tx = await db.Database.BeginTransactionAsync();
                    await i.Lock();
                    if (!await db.PlatformSettings.AnyAsync(x => x.RegistrationEnabled))
                        throw new YardHttpException(403, "registration_closed");
                    var p =
                        await db.PendingExternals.SingleOrDefaultAsync(x => x.Browser == browser)
                        ?? throw new YardHttpException(400, "github_registration_missing");
                    if (p.ExpiresAt < DateTimeOffset.UtcNow)
                        throw new YardHttpException(400, "github_registration_expired");
                    if (
                        !p.EmailVerified
                        || !string.Equals(p.Email, email, StringComparison.OrdinalIgnoreCase)
                    ) {
                        var proof = await db.EmailChallenges.SingleOrDefaultAsync(x =>
                            x.Id == b.ChallengeId
                            && x.Purpose == "github-registration"
                            && x.Browser == browser
                            && x.Email == email
                        );
                        if (
                            proof == null
                            || proof.Consumed
                            || proof.VerifiedAt == null
                            || proof.VerifiedAt < DateTimeOffset.UtcNow.AddMinutes(-5)
                        )
                            throw new YardHttpException(400, "email_verification_required");
                        proof.Consumed = true;
                    }
                    if (await users.FindByEmailAsync(email) != null)
                        throw new YardHttpException(409, "email_exists_sign_in_and_link");
                    if (await users.FindByLoginAsync("github", p.ProviderKey) != null)
                        throw new YardHttpException(409, "github_already_linked");
                    var u = await i.Create(
                        b.Username,
                        string.IsNullOrWhiteSpace(b.Password) ? null : b.Password
                    );
                    u.Email = email;
                    u.EmailConfirmed = true;
                    IdentityOperations.Check(await users.UpdateAsync(u));
                    IdentityOperations.Check(
                        await users.AddLoginAsync(
                            u,
                            new UserLoginInfo("github", p.ProviderKey, "GitHub")
                        )
                    );
                    db.PendingExternals.Remove(p);
                    i.Audit("identity.github.registered", u.Id.ToString(), u.Id);
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                    await IdentityEndpoints.SignIn(h, u);
                    return Results.Ok(new { u.Id });
                }
            )
            .RequireRateLimiting("identity");
        auth.MapDelete(
                "/github",
                async (
                    [Microsoft.AspNetCore.Mvc.FromBody] SensitiveBody b,
                    HttpContext h,
                    IConfiguration c,
                    IdentityOperations i,
                    YardDbContext db,
                    UserManager<YardUser> users
                ) => {
                    Enabled(c);
                    await using var tx = await db.Database.BeginTransactionAsync();
                    await i.Lock();
                    await i.VerifyPasswordOrProof(h, b.Password);
                    var u = await i.Current(h);
                    if (!await users.HasPasswordAsync(u))
                        throw new YardHttpException(409, "password_required_before_unlink");
                    var login =
                        (await users.GetLoginsAsync(u)).SingleOrDefault(x =>
                            x.LoginProvider == "github"
                        ) ?? throw new YardHttpException(404, "github_not_linked");
                    IdentityOperations.Check(
                        await users.RemoveLoginAsync(u, login.LoginProvider, login.ProviderKey)
                    );
                    await i.Revoke(u);
                    i.Audit("identity.github.unlinked", u.Id.ToString(), u.Id);
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                    return Results.NoContent();
                }
            )
            .RequireAuthorization();
    }

    private static IResult Start(HttpContext h, YardUser? user) {
        var properties = new AuthenticationProperties {
            RedirectUri = "/api/auth/external/github/callback",
        };
        properties.Items[OpenIddictClientAspNetCoreConstants.Properties.ProviderName] = "github";
        properties.Items["yard:browser"] = EmailVerification.Browser(h);
        properties.Items["yard:purpose"] = user == null ? "login" : "link";
        if (user != null) {
            properties.Items["yard:user"] = user.Id.ToString();
            properties.Items["yard:stamp"] = user.SecurityStamp;
        }
        return Results.Challenge(
            properties,
            [OpenIddictClientAspNetCoreDefaults.AuthenticationScheme]
        );
    }
}
