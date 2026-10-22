using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Yard.Tests;

public sealed class GitHubProvider : IAsyncDisposable {
    public WebApplication App { get; private set; } = null!;
    public string Url { get; } = "http://127.0.0.1:" + Yard.LocalSettings.FreePort();
    public string Email { get; set; } = "external@example.test";
    public bool Verified { get; set; } = true;
    public bool PkceChecked { get; private set; }
    private readonly Dictionary<string, string> codes = [];

    public static async Task<GitHubProvider> Create() {
        var p = new GitHubProvider();
        var b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls(p.Url);
        p.App = b.Build();
        p.App.MapGet(
            "/login/oauth/authorize",
            (HttpRequest r) => {
                Assert.Equal("stub-client", r.Query["client_id"]);
                Assert.Equal("S256", r.Query["code_challenge_method"]);
                var code = Guid.NewGuid().ToString("N");
                p.codes[code] = r.Query["code_challenge"]!;
                return Results.Redirect(
                    QueryHelpers.AddQueryString(
                        r.Query["redirect_uri"]!,
                        new Dictionary<string, string?> {
                            ["code"] = code,
                            ["state"] = r.Query["state"],
                        }
                    )
                );
            }
        );
        p.App.MapPost(
            "/login/oauth/access_token",
            async (HttpRequest r) => {
                var f = await r.ReadFormAsync();
                Assert.Equal("stub-client", f["client_id"]);
                Assert.Equal("stub-secret", f["client_secret"]);
                var valid =
                    p.codes.Remove(f["code"]!, out var expected)
                    && expected
                        == WebEncoders.Base64UrlEncode(
                            SHA256.HashData(Encoding.ASCII.GetBytes(f["code_verifier"]!))
                        );
                Assert.True(valid, "OAuth must exchange a single-use code with its PKCE verifier");
                p.PkceChecked = true;
                return Results.Json(
                    new {
                        access_token = "stub-access",
                        token_type = "Bearer",
                        scope = "read:user user:email",
                    }
                );
            }
        );
        p.App.MapGet(
            "/user",
            (HttpRequest r) => {
                Assert.Equal("Bearer stub-access", r.Headers.Authorization);
                return Results.Json(
                    new {
                        id = 12345,
                        login = "external-user",
                        name = "External user",
                        email = p.Email,
                        avatar_url = "https://example.test/avatar.png",
                    }
                );
            }
        );
        p.App.MapGet(
            "/user/emails",
            () =>
                Results.Json(
                    new[]
                    {
                        new
                        {
                            email = p.Email,
                            primary = true,
                            verified = p.Verified,
                        },
                    }
                )
        );
        await p.App.StartAsync();
        return p;
    }

    public async Task<Uri> Authorize(Session c, string action = "login") {
        var r = await c.Raw(
            "/api/auth/external/github/" + action,
            "POST",
            action == "link"
                ? new FormUrlEncodedContent(
                    new Dictionary<string, string> { { "password", "Password-for-tests-123" } }
                )
                : null
        );
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var response = await http.GetAsync(r.Headers.Location);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!;
    }

    public async ValueTask DisposeAsync() {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

public sealed class GitHubTests {
    [Fact]
    public async Task OAuthPkceRegistrationLoginReplayAndPasswordlessProtection() {
        await using var provider = await GitHubProvider.Create();
        await using var f = await Fixture.Create(githubServer: provider.Url);
        var c = f.Client();
        var callback = await provider.Authorize(c);
        var result = await c.Raw(callback.PathAndQuery, "GET");
        Assert.Equal("/github-register", result.Headers.Location?.OriginalString);
        Assert.True(provider.PkceChecked);
        var pending = await c.Get("/api/auth/external/registration");
        Assert.True(pending.GetProperty("emailVerified").GetBoolean());
        await c.Ok(
            "/api/auth/external/registration",
            "POST",
            new {
                username = "external",
                email = provider.Email,
                password = (string?)null,
                challengeId = (string?)null,
            }
        );
        var me = await c.Get("/api/auth/me");
        Assert.False(me.GetProperty("hasPassword").GetBoolean());
        var replay = await c.Raw(callback.PathAndQuery, "GET");
        Assert.False(replay.IsSuccessStatusCode);
        await c.Ok("/api/auth/logout", "POST");
        var second = f.Client();
        var login = await provider.Authorize(second);
        var logged = await second.Raw(login.PathAndQuery, "GET");
        Assert.Equal("/", logged.Headers.Location?.OriginalString);
        await second.Get("/api/auth/me");
        var proof = await second.Ok("/api/auth/email/request", "POST", new { purpose = "reauth" });
        var id = (await proof.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("challengeId")
            .GetString();
        await second.Ok(
            "/api/auth/email/verify",
            "POST",
            new { challengeId = id, code = f.Code(provider.Email) }
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await second.Raw(
                    "/api/auth/external/github",
                    "DELETE",
                    new { password = (string?)null }
                )
            ).StatusCode
        );
        await second.Ok(
            "/api/auth/change-password",
            "POST",
            new { currentPassword = (string?)null, password = "New-local-password-123" }
        );
        await f.Client()
            .Ok(
                "/api/auth/login",
                "POST",
                new { identifier = "external", password = "New-local-password-123" }
            );
    }

    [Fact]
    public async Task ExplicitLinkLoginAndUnlinkKeepTheExistingIdentity() {
        await using var provider = await GitHubProvider.Create();
        await using var f = await Fixture.Create(githubServer: provider.Url);
        var (c, id) = await f.User("existing");
        var callback = await provider.Authorize(c, "link");
        var result = await c.Raw(callback.PathAndQuery, "GET");
        Assert.Equal("/login?notice=github-linked", result.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Raw("/api/auth/me", "GET")).StatusCode);
        var login = f.Client();
        var second = await provider.Authorize(login);
        await login.Raw(second.PathAndQuery, "GET");
        Assert.Equal(id, (await login.Get("/api/auth/me")).GetProperty("id").GetGuid());
        await login.Ok(
            "/api/auth/external/github",
            "DELETE",
            new { password = "Password-for-tests-123" }
        );
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await login.Raw("/api/auth/me", "GET")).StatusCode
        );
        var local = f.Client();
        await local.Ok(
            "/api/auth/login",
            "POST",
            new { identifier = "existing", password = "Password-for-tests-123" }
        );
        Assert.Empty(
            (await local.Get("/api/auth/me")).GetProperty("externalLogins").EnumerateArray()
        );
    }

    [Fact]
    public async Task GithubWithoutVerifiedProviderEmailRequiresOurOneTimeProof() {
        await using var provider = await GitHubProvider.Create();
        provider.Verified = false;
        await using var f = await Fixture.Create(githubServer: provider.Url);
        var c = f.Client();
        var callback = await provider.Authorize(c);
        await c.Raw(callback.PathAndQuery, "GET");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.Raw(
                    "/api/auth/external/registration",
                    "POST",
                    new {
                        username = "external",
                        email = provider.Email,
                        password = (string?)null,
                        challengeId = (string?)null,
                    }
                )
            ).StatusCode
        );
        var send = await c.Ok(
            "/api/auth/email/request",
            "POST",
            new { purpose = "github-registration", email = provider.Email }
        );
        var id = (await send.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("challengeId")
            .GetString();
        await c.Ok(
            "/api/auth/email/verify",
            "POST",
            new { challengeId = id, code = f.Code(provider.Email) }
        );
        await c.Ok(
            "/api/auth/external/registration",
            "POST",
            new {
                username = "external",
                email = provider.Email,
                password = (string?)null,
                challengeId = id,
            }
        );
        Assert.True((await c.Get("/api/auth/me")).GetProperty("emailConfirmed").GetBoolean());
    }

    [Fact]
    public async Task SameVerifiedEmailDoesNotAutomaticallyMergeAccounts() {
        await using var provider = await GitHubProvider.Create();
        await using var f = await Fixture.Create(githubServer: provider.Url);
        var (existing, id) = await f.User("existing");
        await using (var scope = f.App.Services.CreateAsyncScope()) {
            var users =
                scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Yard.AspNetCore.YardUser>>();
            var u = await users.FindByIdAsync(id.ToString());
            u!.Email = provider.Email;
            u.EmailConfirmed = true;
            await users.UpdateAsync(u);
        }
        var c = f.Client();
        var callback = await provider.Authorize(c);
        await c.Raw(callback.PathAndQuery, "GET");
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await c.Raw(
                    "/api/auth/external/registration",
                    "POST",
                    new {
                        username = "external",
                        email = provider.Email,
                        password = (string?)null,
                        challengeId = (string?)null,
                    }
                )
            ).StatusCode
        );
    }
}
