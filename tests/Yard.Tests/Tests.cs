using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Yard;
using Yard.AspNetCore;

namespace Yard.Tests;

public sealed class WorkspaceTests {
    [Fact]
    public void RejectsUnknownProperties() {
        var root = Temp();
        File.WriteAllText(Path.Combine(root, "yard.json"), "{\"name\":\"Test\",\"commands\":{}}");
        Assert.Throws<YardException>(() => Workspace.Load(root));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("Api")]
    [InlineData("bad_name")]
    [InlineData("web")]
    public void RejectsInvalidOrDuplicateApplication(string name) {
        var w = new Workspace(Temp(), new() { Name = "Test", Backends = ["api", name] });
        Assert.Throws<YardException>(w.Validate);
    }

    [Fact]
    public void DetectsCycles() {
        var w = new Workspace(
            Temp(),
            new() {
                Name = "Test",
                Backends = ["api", "manager"],
                Dependencies = new() { ["api"] = ["manager"], ["manager"] = ["api"] },
            }
        );
        Assert.Throws<YardException>(w.Validate);
    }

    [Fact]
    public void BackendOrderAndAppSelectionAreStable() {
        var w = new Workspace(
            Temp(),
            new() {
                Name = "Test",
                Backends = ["api", "manager"],
                Frontends = ["web", "admin"],
                Dependencies = new() { ["api"] = ["manager"] },
            }
        );
        w.Validate();
        Assert.Equal(["manager", "api"], w.OrderedBackends());
        Assert.Equal(["admin", "api", "manager"], w.DevelopmentApps("admin"));
        Assert.Equal("Test.Manager.Api", w.ProjectName("manager"));
    }

    [Fact]
    public void SettingsAreIdempotentAndCheckoutSpecific() {
        var root = Temp();
        var w = new Workspace(root, new() { Name = "Test", Database = "sqlite" });
        var a = LocalSettings.Configure(w);
        var b = LocalSettings.Configure(w);
        Assert.Equal(a.ControlToken, b.ControlToken);
        Assert.Equal(a.Ports["api"], b.Ports["api"]);
        Assert.NotEqual(a.Identity, new Workspace(Temp(), w.Definition).Identity);
        Assert.Contains(root, a.Connections["api"]);
    }

    [Fact]
    public void SyncCheckDoesNotWrite() {
        var w = new Workspace(Temp(), new() { Name = "Test" });
        Assert.NotEmpty(Synchronization.Apply(w, true));
        Assert.False(File.Exists(Path.Combine(w.Root, "Test.slnx")));
        Synchronization.Apply(w, false);
        Assert.Empty(Synchronization.Apply(w, true));
    }

    [Fact]
    public void TailwindSourcesOnlyFollowDeclaredFrontends() {
        var w = new Workspace(
            Temp(),
            new() {
                Name = "Test",
                Backends = ["api", "manager"],
                Frontends = ["web", "admin"],
            }
        );
        var sources = Synchronization.Expected(w)["packages/ui/src/sources.css"];
        Assert.Contains("apps/web/src/", sources);
        Assert.Contains("apps/admin/src/", sources);
        Assert.DoesNotContain("apps/api", sources);
        Assert.DoesNotContain("apps/manager", sources);
        Assert.DoesNotContain("apps/*", sources);
    }

    [Fact]
    public void CopiedControlSettingsCannotStopAnotherCheckout() {
        var source = new Workspace(Temp(), new() { Name = "Test", Database = "sqlite" });
        LocalSettings.Configure(source);
        var other = new Workspace(Temp(), source.Definition);
        Directory.CreateDirectory(other.State);
        File.Copy(LocalSettings.PathFor(source), LocalSettings.PathFor(other));
        Assert.Throws<YardException>(() => LocalSettings.Load(other));
    }

    private static string Temp() {
        var p = Path.Combine(FindRoot(), ".temp", "unit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    public static string FindRoot() {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Yard.slnx")))
            d = d.Parent;
        return d!.FullName;
    }
}

public sealed class TenantNote : IOrganizationEntity {
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Text { get; set; } = "";
}

public sealed class FixtureDb(DbContextOptions<FixtureDb> options, OrganizationScope scope)
    : YardDbContext(options, scope) {
    public DbSet<TenantNote> Notes => Set<TenantNote>();

    protected override void OnModelCreating(ModelBuilder builder) {
        base.OnModelCreating(builder);
        builder.Entity<TenantNote>();
    }
}

public sealed class FailingSender : IEmailSender {
    public Task SendAsync(
        string recipient,
        string subject,
        string text,
        CancellationToken ct = default
    ) => throw new IOException("controlled SMTP failure");
}

public sealed class Fixture : IAsyncDisposable {
    public WebApplication App { get; private set; } = null!;
    public string Root { get; } =
        Path.Combine(WorkspaceTests.FindRoot(), ".temp", "runtime", Guid.NewGuid().ToString("N"));

    public static async Task<Fixture> Create(bool failMail = false, string? githubServer = null) {
        var f = new Fixture();
        Directory.CreateDirectory(f.Root);
        var b = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = "Development", ContentRootPath = f.Root }
        );
        b.Logging.ClearProviders();
        b.WebHost.UseTestServer();
        b.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> {
                ["Yard:Name"] = "Fixture",
                ["Yard:Root"] = f.Root,
                ["Yard:Database"] = "sqlite",
                ["Yard:ConnectionString"] =
                    "Data Source=" + Path.Combine(f.Root, "test.db") + ";Default Timeout=30",
                ["Yard:VerificationKey"] = Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)
                ),
                ["Yard:PublicOrigin"] = "http://localhost",
            }
        );
        if (githubServer != null)
            b.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?> {
                    ["Yard:GitHub:Enabled"] = "true",
                    ["Yard:GitHub:ClientId"] = "stub-client",
                    ["Yard:GitHub:ClientSecret"] = "stub-secret",
                    ["Yard:GitHub:DevelopmentServer"] = githubServer,
                }
            );
        b.AddYard<FixtureDb>();
        if (failMail)
            b.Services.AddSingleton<IEmailSender, FailingSender>();
        f.App = b.Build();
        f.App.UseYard();
        await using (var s = f.App.Services.CreateAsyncScope()) {
            await s
                .ServiceProvider.GetRequiredService<YardDbContext>()
                .Database.EnsureCreatedAsync();
        }
        await f.App.StartAsync();
        return f;
    }

    public Session Client() => new(App.GetTestClient());

    public async Task<(Session, Guid)> User(string username, bool admin = false) {
        await using (var s = App.Services.CreateAsyncScope()) {
            var i = s.ServiceProvider.GetRequiredService<IdentityOperations>();
            await i.Create(username, "Password-for-tests-123", admin);
        }
        var c = Client();
        await c.Ok(
            "/api/auth/login",
            "POST",
            new { identifier = username, password = "Password-for-tests-123" }
        );
        var me = await c.Get("/api/auth/me");
        return (c, me.GetProperty("id").GetGuid());
    }

    public string Code(string email) {
        var mail = Directory
            .GetFiles(Path.Combine(Root, ".yard", "mail"))
            .Select(x => JsonSerializer.Deserialize<CapturedMail>(File.ReadAllText(x))!)
            .Last(x => x.Recipient == email);
        return System.Text.RegularExpressions.Regex.Match(mail.Text, "\\d{6}").Value;
    }

    public async ValueTask DisposeAsync() {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

public sealed class Session(HttpClient http) {
    private readonly Dictionary<string, string> cookies = [];

    private async Task<HttpResponseMessage> Send(
        string path,
        string method,
        object? body = null,
        bool csrf = true
    ) {
        string? token = null;
        if (method != "GET" && csrf) {
            var c = await Send("/api/auth/csrf", "GET");
            token = (await c.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("token")
                .GetString();
        }
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            string.Join("; ", cookies.Select(x => x.Key + "=" + x.Value))
        );
        if (token != null)
            request.Headers.Add("X-CSRF-TOKEN", token);
        if (body != null)
            request.Content = body is HttpContent content ? content : JsonContent.Create(body);
        var response = await http.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            foreach (var value in values) {
                var pair = value.Split(';')[0].Split('=', 2);
                cookies[pair[0]] = pair[1];
            }
        return response;
    }

    public Task<HttpResponseMessage> Raw(
        string path,
        string method,
        object? body = null,
        bool csrf = true
    ) => Send(path, method, body, csrf);

    public async Task<JsonElement> Get(string path) {
        var r = await Send(path, "GET");
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<HttpResponseMessage> Ok(string path, string method, object? body = null) {
        var r = await Send(path, method, body);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return r;
    }
}

public sealed class RuntimeTests {
    [Fact]
    public async Task DevelopmentCookiesAreIndependentAcrossCheckoutsOfTheSameProject() {
        await using var a = await Fixture.Create();
        await using var b = await Fixture.Create();
        var first = a.App.Services.GetRequiredService<YardSettings>();
        var second = b.App.Services.GetRequiredService<YardSettings>();
        Assert.NotEqual(first.CookieScope, second.CookieScope);
        var ar = await a.Client().Raw("/api/auth/csrf", "GET");
        var br = await b.Client().Raw("/api/auth/csrf", "GET");
        Assert.Contains(ar.Headers.GetValues("Set-Cookie"), x => x.Contains(first.CookieScope));
        Assert.Contains(br.Headers.GetValues("Set-Cookie"), x => x.Contains(second.CookieScope));
    }

    [Fact]
    public async Task UsernameRegistrationAndLoginWorkWithoutEmail() {
        await using var f = await Fixture.Create();
        var c = f.Client();
        await c.Ok(
            "/api/auth/register",
            "POST",
            new { username = "alice", password = "Password-for-tests-123" }
        );
        await c.Ok(
            "/api/auth/login",
            "POST",
            new { identifier = "ALICE", password = "Password-for-tests-123" }
        );
        var me = await c.Get("/api/auth/me");
        Assert.Equal("alice", me.GetProperty("username").GetString());
        Assert.False(me.GetProperty("emailConfirmed").GetBoolean());
    }

    [Fact]
    public async Task CsrfIsRequiredForAnonymousRegistration() {
        await using var f = await Fixture.Create();
        var r = await f.Client()
            .Raw(
                "/api/auth/register",
                "POST",
                new { username = "alice", password = "Password-for-tests-123" },
                false
            );
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task DuplicateUsernameAndClosedRegistrationAreRejected() {
        await using var f = await Fixture.Create();
        var (admin, _) = await f.User("owner", true);
        await admin.Ok("/api/admin/registration", "PATCH", new { enabled = false });
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await f.Client()
                    .Raw(
                        "/api/auth/register",
                        "POST",
                        new { username = "alice", password = "Password-for-tests-123" }
                    )
            ).StatusCode
        );
        await admin.Ok("/api/admin/registration", "PATCH", new { enabled = true });
        var c = f.Client();
        await c.Ok(
            "/api/auth/register",
            "POST",
            new { username = "alice", password = "Password-for-tests-123" }
        );
        Assert.False(
            (
                await c.Raw(
                    "/api/auth/register",
                    "POST",
                    new { username = "ALICE", password = "Password-for-tests-123" }
                )
            ).IsSuccessStatusCode
        );
    }

    [Fact]
    public async Task DisableImmediatelyRevokesSessionsAndLastAdminIsProtected() {
        await using var f = await Fixture.Create();
        var (admin, aid) = await f.User("owner", true);
        var (member, id) = await f.User("alice");
        await admin.Ok($"/api/admin/users/{id}/state", "PATCH", new { enabled = false });
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await member.Raw("/api/auth/me", "GET")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await admin.Raw($"/api/admin/users/{aid}/state", "PATCH", new { enabled = false })
            ).StatusCode
        );
    }

    [Fact]
    public async Task EmailVerificationChangesLoginAndConsumesChallenge() {
        await using var f = await Fixture.Create();
        var (c, _) = await f.User("alice");
        var r = await c.Ok(
            "/api/auth/email/request",
            "POST",
            new {
                purpose = "bind-email",
                email = "alice@example.test",
                password = "Password-for-tests-123",
            }
        );
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("challengeId")
            .GetString();
        var code = f.Code("alice@example.test");
        Assert.Equal(6, code.Length);
        await c.Ok("/api/auth/email/verify", "POST", new { challengeId = id, code });
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Raw("/api/auth/me", "GET")).StatusCode);
        var fresh = f.Client();
        await fresh.Ok(
            "/api/auth/login",
            "POST",
            new { identifier = "ALICE@example.test", password = "Password-for-tests-123" }
        );
        Assert.False(
            (
                await fresh.Raw("/api/auth/email/verify", "POST", new { challengeId = id, code })
            ).IsSuccessStatusCode
        );
    }

    [Fact]
    public async Task EmailChallengesAreBrowserBoundAndAttemptsAreLimited() {
        await using var f = await Fixture.Create();
        var (c, _) = await f.User("alice");
        var r = await c.Ok(
            "/api/auth/email/request",
            "POST",
            new {
                purpose = "bind-email",
                email = "alice@example.test",
                password = "Password-for-tests-123",
            }
        );
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("challengeId")
            .GetString();
        var (codeClient, _2) = await f.User("bob");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await codeClient.Raw(
                    "/api/auth/email/verify",
                    "POST",
                    new { challengeId = id, code = f.Code("alice@example.test") }
                )
            ).StatusCode
        );
        for (var n = 0; n < 5; n++)
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (
                    await c.Raw(
                        "/api/auth/email/verify",
                        "POST",
                        new { challengeId = id, code = "invalid" }
                    )
                ).StatusCode
            );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.Raw(
                    "/api/auth/email/verify",
                    "POST",
                    new { challengeId = id, code = f.Code("alice@example.test") }
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task FailedDeliveryRollsBackUsableChallenge() {
        await using var f = await Fixture.Create(true);
        var (c, _) = await f.User("alice");
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (
                await c.Raw(
                    "/api/auth/email/request",
                    "POST",
                    new {
                        purpose = "bind-email",
                        email = "alice@example.test",
                        password = "Password-for-tests-123",
                    }
                )
            ).StatusCode
        );
        await using var scope = f.App.Services.CreateAsyncScope();
        Assert.Empty(
            await scope
                .ServiceProvider.GetRequiredService<YardDbContext>()
                .EmailChallenges.ToListAsync()
        );
    }

    [Fact]
    public async Task VerifiedPhoneSupportsPasswordLogin() {
        await using var f = await Fixture.Create();
        var (admin, _) = await f.User("owner", true);
        var (c, id) = await f.User("alice");
        await admin.Ok(
            $"/api/admin/users/{id}/phone",
            "PATCH",
            new {
                phoneNumber = "13800138000",
                ownershipConfirmed = true,
                password = "Password-for-tests-123",
            }
        );
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Raw("/api/auth/me", "GET")).StatusCode);
        await f.Client()
            .Ok(
                "/api/auth/login",
                "POST",
                new { identifier = "13800138000", password = "Password-for-tests-123" }
            );
    }

    [Fact]
    public async Task OrganizationInviteAndImmediateRoleChangesAreIsolated() {
        await using var f = await Fixture.Create();
        var (owner, oid) = await f.User("owner");
        var (member, mid) = await f.User("alice");
        var first = await owner.Ok("/api/organizations/", "POST", new { name = "First" });
        var id = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var second = await owner.Ok("/api/organizations/", "POST", new { name = "Second" });
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetGuid();
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await member.Raw($"/api/organizations/{id}/members", "GET")).StatusCode
        );
        await owner.Ok(
            $"/api/organizations/{id}/invitations",
            "POST",
            new { identifier = "alice" }
        );
        var invites = await member.Get("/api/invitations");
        await member.Ok(
            "/api/invitations/" + invites[0].GetProperty("id").GetString() + "/accept",
            "POST"
        );
        await member.Get($"/api/organizations/{id}/members");
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await member.Raw($"/api/organizations/{id}", "PATCH", new { name = "Denied" })
            ).StatusCode
        );
        var roleResponse = await owner.Ok(
            $"/api/organizations/{id}/roles",
            "POST",
            new {
                name = "Editor",
                permissions = new[] { "organization.read", "organization.update" },
            }
        );
        var rid = (await roleResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id")
            .GetGuid();
        await owner.Ok(
            $"/api/organizations/{id}/members/{mid}/roles",
            "PUT",
            new { roleIds = new[] { rid } }
        );
        await member.Ok($"/api/organizations/{id}", "PATCH", new { name = "Updated" });
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await member.Raw($"/api/organizations/{secondId}", "GET")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await owner.Raw($"/api/organizations/{id}/members/{oid}", "DELETE")).StatusCode
        );
    }

    [Fact]
    public async Task OwnershipTransferAndForeignRolesRemainOrganizationBound() {
        await using var f = await Fixture.Create();
        var (owner, oldOwnerId) = await f.User("owner");
        var (member, newOwnerId) = await f.User("member");
        var (_, outsiderId) = await f.User("outsider");
        async Task<Guid> Create(string name) =>
            (
                await (
                    await owner.Ok("/api/organizations/", "POST", new { name })
                ).Content.ReadFromJsonAsync<JsonElement>()
            )
                .GetProperty("id")
                .GetGuid();
        var first = await Create("First");
        var second = await Create("Second");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await owner.Raw(
                    $"/api/organizations/{first}/transfer",
                    "POST",
                    new { userId = outsiderId, password = "Password-for-tests-123" }
                )
            ).StatusCode
        );
        await owner.Ok(
            $"/api/organizations/{first}/invitations",
            "POST",
            new { identifier = "member" }
        );
        var invitation = (await member.Get("/api/invitations"))[0].GetProperty("id").GetGuid();
        await member.Ok($"/api/invitations/{invitation}/accept", "POST");
        var foreignRole = (await owner.Get($"/api/organizations/{second}/roles"))[0]
            .GetProperty("id")
            .GetGuid();
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await owner.Raw(
                    $"/api/organizations/{first}/members/{newOwnerId}/roles",
                    "PUT",
                    new { roleIds = new[] { foreignRole } }
                )
            ).StatusCode
        );
        await owner.Ok(
            $"/api/organizations/{first}/transfer",
            "POST",
            new { userId = newOwnerId, password = "Password-for-tests-123" }
        );
        Assert.Equal(
            newOwnerId,
            (await member.Get($"/api/organizations/{first}")).GetProperty("ownerId").GetGuid()
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (
                await owner.Raw($"/api/organizations/{first}", "PATCH", new { name = "Denied" })
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await member.Raw($"/api/organizations/{first}/members/{newOwnerId}", "DELETE")
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await member.Raw($"/api/organizations/{second}", "GET")).StatusCode
        );
        await member.Ok($"/api/organizations/{first}/archive", "PATCH", new { archived = true });
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await member.Raw($"/api/organizations/{first}/members", "GET")).StatusCode
        );
        await member.Ok($"/api/organizations/{first}/archive", "PATCH", new { archived = false });
        Assert.Contains(
            (await member.Get($"/api/organizations/{first}/members")).EnumerateArray(),
            x => x.GetProperty("id").GetGuid() == oldOwnerId
        );
    }

    [Fact]
    public async Task InvitationsRejectOtherAccountsExpiryRevocationAndCrossOrganizationWrites() {
        await using var f = await Fixture.Create();
        var (owner, _) = await f.User("owner");
        var (member, _) = await f.User("member");
        var (outsider, _) = await f.User("outsider");
        async Task<Guid> Create(string name) =>
            (
                await (
                    await owner.Ok("/api/organizations/", "POST", new { name })
                ).Content.ReadFromJsonAsync<JsonElement>()
            )
                .GetProperty("id")
                .GetGuid();
        var first = await Create("First");
        var second = await Create("Second");
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await owner.Raw(
                    $"/api/organizations/{first}/invitations",
                    "POST",
                    new { identifier = "not-registered" }
                )
            ).StatusCode
        );
        async Task<Guid> Send() =>
            (
                await (
                    await owner.Ok(
                        $"/api/organizations/{first}/invitations",
                        "POST",
                        new { identifier = "member" }
                    )
                ).Content.ReadFromJsonAsync<JsonElement>()
            )
                .GetProperty("id")
                .GetGuid();
        var id = await Send();
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await outsider.Raw($"/api/invitations/{id}/accept", "POST")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await owner.Raw($"/api/organizations/{second}/invitations/{id}", "DELETE")).StatusCode
        );
        await using (var scope = f.App.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<YardDbContext>();
            var expiredAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db
                .OrganizationInvitations.IgnoreQueryFilters()
                .Where(x => x.Id == id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.ExpiresAt, expiredAt));
        }
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await member.Raw($"/api/invitations/{id}/accept", "POST")).StatusCode
        );
        Assert.Equal(id, await Send());
        await owner.Ok($"/api/organizations/{first}/invitations/{id}", "DELETE");
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await member.Raw($"/api/invitations/{id}/accept", "POST")).StatusCode
        );
        var renewed = await Send();
        Assert.NotEqual(id, renewed);
        await member.Ok($"/api/invitations/{renewed}/accept", "POST");
        await member.Ok($"/api/invitations/{renewed}/accept", "POST");
        Assert.Single((await member.Get("/api/organizations/")).EnumerateArray());
    }

    [Fact]
    public async Task TenantWritesRequireScope() {
        await using var f = await Fixture.Create();
        await using var scope = f.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YardDbContext>();
        db.OrganizationRoles.Add(new() { OrganizationId = Guid.NewGuid(), Name = "escape" });
        await Assert.ThrowsAsync<YardHttpException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task InvalidBodiesAreRejectedBeforeIdentityLogic() {
        await using var f = await Fixture.Create();
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await f.Client().Raw("/api/auth/register", "POST", new { username = "alice" })
            ).StatusCode
        );
    }

    [Fact]
    public async Task PasswordRecoveryConsumesOnlyItsOwnProof() {
        await using var f = await Fixture.Create();
        var (c, id) = await f.User("alice");
        await using (var scope = f.App.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<YardUser>>();
            var u = await users.FindByIdAsync(id.ToString());
            u!.Email = "alice@example.test";
            u.EmailConfirmed = true;
            IdentityOperations.Check(await users.UpdateAsync(u));
        }
        var recovery = f.Client();
        var r = await recovery.Ok(
            "/api/auth/email/request",
            "POST",
            new { purpose = "recover-password", email = "alice@example.test" }
        );
        var challenge = (await r.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("challengeId")
            .GetString();
        await recovery.Ok(
            "/api/auth/email/verify",
            "POST",
            new { challengeId = challenge, code = f.Code("alice@example.test") }
        );
        await recovery.Ok(
            "/api/auth/recover-password",
            "POST",
            new { challengeId = challenge, password = "Recovered-password-123" }
        );
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Raw("/api/auth/me", "GET")).StatusCode);
        await f.Client()
            .Ok(
                "/api/auth/login",
                "POST",
                new { identifier = "alice", password = "Recovered-password-123" }
            );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await recovery.Raw(
                    "/api/auth/recover-password",
                    "POST",
                    new { challengeId = challenge, password = "Recovered-password-456" }
                )
            ).StatusCode
        );
    }

    [Fact]
    public async Task ExpiryResendAndRateLimitInvalidateTheOldCode() {
        await using var f = await Fixture.Create();
        var (c, _) = await f.User("alice");
        var r = await c.Ok(
            "/api/auth/email/request",
            "POST",
            new {
                purpose = "bind-email",
                email = "alice@example.test",
                password = "Password-for-tests-123",
            }
        );
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("challengeId")
            .GetString();
        var code = f.Code("alice@example.test");
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (
                await c.Raw(
                    "/api/auth/email/request",
                    "POST",
                    new {
                        purpose = "bind-email",
                        email = "alice@example.test",
                        password = "Password-for-tests-123",
                    }
                )
            ).StatusCode
        );
        await using (var scope = f.App.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<YardDbContext>();
            var old = await db.EmailChallenges.SingleAsync();
            Assert.DoesNotContain(code, old.Digest);
            old.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-6);
            old.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await c.Raw("/api/auth/email/verify", "POST", new { challengeId = id, code })
            ).StatusCode
        );
        await c.Ok(
            "/api/auth/email/request",
            "POST",
            new {
                purpose = "bind-email",
                email = "alice@example.test",
                password = "Password-for-tests-123",
            }
        );
        await using (var scope = f.App.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<YardDbContext>();
            Assert.True((await db.EmailChallenges.SingleAsync(x => x.Id == id)).Consumed);
        }
    }

    [Fact]
    public async Task ConcurrentCodeConsumptionSucceedsOnlyOnce() {
        await using var f = await Fixture.Create();
        var (c, _) = await f.User("alice");
        var r = await c.Ok(
            "/api/auth/email/request",
            "POST",
            new {
                purpose = "bind-email",
                email = "alice@example.test",
                password = "Password-for-tests-123",
            }
        );
        var id = (await r.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("challengeId")
            .GetString();
        var code = f.Code("alice@example.test");
        var responses = await Task.WhenAll(
            c.Raw("/api/auth/email/verify", "POST", new { challengeId = id, code }),
            c.Raw("/api/auth/email/verify", "POST", new { challengeId = id, code })
        );
        Assert.Single(responses, x => x.IsSuccessStatusCode);
    }

    [Fact]
    public async Task ConcurrentRegistrationReservesAUniqueCaseInsensitiveUsername() {
        await using var f = await Fixture.Create();
        var a = f.Client();
        var b = f.Client();
        var results = await Task.WhenAll(
            a.Raw(
                "/api/auth/register",
                "POST",
                new { username = "alice", password = "Password-for-tests-123" }
            ),
            b.Raw(
                "/api/auth/register",
                "POST",
                new { username = "ALICE", password = "Password-for-tests-123" }
            )
        );
        Assert.Single(results, x => x.IsSuccessStatusCode);
    }

    [Fact]
    public async Task PlatformAdministratorGrantAndLastDemotionAreProtected() {
        await using var f = await Fixture.Create();
        var (admin, aid) = await f.User("owner", true);
        var (member, id) = await f.User("alice");
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await admin.Raw(
                    $"/api/admin/users/{aid}/administrator",
                    "PATCH",
                    new { administrator = false, password = "Password-for-tests-123" }
                )
            ).StatusCode
        );
        await admin.Ok(
            $"/api/admin/users/{id}/administrator",
            "PATCH",
            new { administrator = true, password = "Password-for-tests-123" }
        );
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await member.Raw("/api/auth/me", "GET")).StatusCode
        );
        await admin.Ok(
            $"/api/admin/users/{aid}/administrator",
            "PATCH",
            new { administrator = false, password = "Password-for-tests-123" }
        );
    }

    [Fact]
    public async Task BusinessEntitiesRegisteredAfterBaseMappingAreIsolatedInQueriesAndDetachedWrites() {
        await using var f = await Fixture.Create();
        var (owner, uid) = await f.User("owner");
        var a = await owner.Ok("/api/organizations/", "POST", new { name = "A" });
        var aid = (await a.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var b = await owner.Ok("/api/organizations/", "POST", new { name = "B" });
        var bid = (await b.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var service = f.App.Services.CreateAsyncScope();
        var db = service.ServiceProvider.GetRequiredService<FixtureDb>();
        var authorization = service.ServiceProvider.GetRequiredService<OrganizationOperations>();
        var h = new Microsoft.AspNetCore.Http.DefaultHttpContext {
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [
                        new System.Security.Claims.Claim(
                            System.Security.Claims.ClaimTypes.NameIdentifier,
                            uid.ToString()
                        ),
                    ],
                    "test"
                )
            ),
        };
        await authorization.Require(h, aid, "organization.read");
        var note = new TenantNote { OrganizationId = aid, Text = "Private A" };
        db.Notes.Add(note);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await authorization.Require(h, bid, "organization.read");
        Assert.Empty(await db.Notes.AsNoTracking().ToListAsync());
        db.Notes.Update(
            new TenantNote {
                Id = note.Id,
                OrganizationId = bid,
                Text = "Attempted escape",
            }
        );
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        await authorization.Require(h, aid, "organization.read");
        Assert.Equal("Private A", (await db.Notes.SingleAsync()).Text);
    }

    [Fact]
    public async Task GithubDisabledDoesNotExposeAnActiveProvider() {
        await using var f = await Fixture.Create();
        var c = f.Client();
        var opts = await c.Get("/api/auth/options");
        Assert.False(opts.GetProperty("githubEnabled").GetBoolean());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await c.Raw("/api/auth/external/github/login", "POST")).StatusCode
        );
    }
}
