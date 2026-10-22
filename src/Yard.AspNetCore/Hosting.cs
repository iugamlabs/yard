using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Client;

namespace Yard.AspNetCore;

public static class YardHosting {
    public static WebApplicationBuilder AddYard<T>(this WebApplicationBuilder b)
        where T : YardDbContext {
        var root =
            Environment.GetEnvironmentVariable("YARD_ROOT")
            ?? b.Configuration["Yard:Root"]
            ?? b.Environment.ContentRootPath;
        var app =
            Environment.GetEnvironmentVariable("YARD_APP") ?? b.Configuration["Yard:App"] ?? "api";
        var localPath = Path.Combine(root, ".yard", "settings.json");
        var settings = new YardSettings { Root = root, App = app };
        if (File.Exists(Path.Combine(root, "yard.json"))) {
            var d = JsonDocument
                .Parse(File.ReadAllText(Path.Combine(root, "yard.json")))
                .RootElement;
            settings.Name = d.GetProperty("name").GetString()!;
            settings.Database = d.GetProperty("database").GetString()!;
        }
        if (b.Environment.IsDevelopment() && File.Exists(localPath)) {
            var local = JsonDocument.Parse(File.ReadAllText(localPath)).RootElement;
            settings.ConnectionString = local
                .GetProperty("connections")
                .GetProperty(app)
                .GetString()!;
            settings.VerificationKey = local.GetProperty("verificationKey").GetString()!;
            settings.PublicOrigin =
                "http://localhost:"
                + (
                    Environment.GetEnvironmentVariable("YARD_PORT")
                    ?? local.GetProperty("gatewayPort").GetInt32().ToString()
                );
        }
        b.Configuration.AddJsonFile(
                Path.Combine(root, ".yard", "appsettings.local.json"),
                optional: true
            )
            .AddEnvironmentVariables();
        b.Configuration.GetSection("Yard").Bind(settings);
        settings.CookieScope =
            settings.Name.ToLowerInvariant().Replace('.', '-')
            + (
                b.Environment.IsDevelopment()
                    ? "-"
                        + Convert.ToHexStringLower(
                            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root)))
                        )[..12]
                    : ""
            );
        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
            throw new InvalidOperationException(
                "Yard database is not configured. Run yard init or configure Yard:ConnectionString."
            );
        if (settings.VerificationKey.Length == 0)
            throw new InvalidOperationException(
                "Yard:VerificationKey is required (a base64-encoded 32-byte key)."
            );
        if (Convert.FromBase64String(settings.VerificationKey).Length != 32)
            throw new InvalidOperationException("Yard:VerificationKey must contain 32 bytes.");
        if (
            !Uri.TryCreate(settings.PublicOrigin, UriKind.Absolute, out var origin)
            || (!b.Environment.IsDevelopment() && origin.Scheme != "https")
        )
            throw new InvalidOperationException(
                "Yard:PublicOrigin must be an absolute origin; production requires HTTPS."
            );
        b.Services.AddSingleton(settings);
        b.Services.AddSingleton(TimeProvider.System);
        b.Services.AddScoped<OrganizationScope>();
        b.Services.AddDbContext<T>(o => {
            if (settings.Database == "sqlite")
                o.UseSqlite(settings.ConnectionString);
            else if (settings.Database == "postgresql")
                o.UseNpgsql(settings.ConnectionString);
            else
                throw new InvalidOperationException("Unsupported database.");
            o.UseOpenIddict();
        });
        b.Services.AddScoped<YardDbContext>(p => p.GetRequiredService<T>());
        b.Services.AddIdentity<YardUser, IdentityRole<Guid>>(o => {
            o.Password.RequiredLength = 12;
            o.Password.RequireDigit = false;
            o.Password.RequireUppercase = false;
            o.Password.RequireLowercase = false;
            o.Password.RequireNonAlphanumeric = false;
            o.User.RequireUniqueEmail = false;
            o.Lockout.MaxFailedAccessAttempts = 5;
            o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        })
            .AddEntityFrameworkStores<T>()
            .AddDefaultTokenProviders();
        var keys =
            b.Configuration["Yard:KeysDirectory"] ?? Path.Combine(root, ".yard", "keys", app);
        Directory.CreateDirectory(keys);
        b.Services.AddDataProtection()
            .SetApplicationName(settings.Name + "." + app)
            .PersistKeysToFileSystem(new DirectoryInfo(keys));
        b.Services.Configure<SecurityStampValidatorOptions>(o =>
            o.ValidationInterval = TimeSpan.Zero
        );
        b.Services.ConfigureApplicationCookie(o => {
            o.Cookie.Name = "yard-" + settings.CookieScope;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = b.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            o.Cookie.Path = "/";
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = false;
            o.Events.OnRedirectToLogin = c => {
                c.Response.StatusCode = 401;
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = c => {
                c.Response.StatusCode = 403;
                return Task.CompletedTask;
            };
            o.Events.OnValidatePrincipal = async c => {
                var manager = c.HttpContext.RequestServices.GetRequiredService<
                    UserManager<YardUser>
                >();
                var u = await manager.GetUserAsync(c.Principal!);
                var stamp = c.Principal!.FindFirstValue("AspNet.Identity.SecurityStamp");
                if (u == null || !u.IsEnabled || u.SecurityStamp != stamp) {
                    c.RejectPrincipal();
                    await c.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                }
            };
        });
        b.Services.AddAuthorization();
        b.Services.AddAntiforgery(o => {
            o.HeaderName = "X-CSRF-TOKEN";
            o.Cookie.Name = "yard-csrf-" + settings.CookieScope;
            o.Cookie.SameSite = SameSiteMode.Strict;
        });
        b.Services.AddRateLimiter(o => {
            o.RejectionStatusCode = 429;
            o.OnRejected = async (context, token) => {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { errorCode = "rate_limited" },
                    token
                );
            };
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(h =>
                RateLimitPartition.GetFixedWindowLimiter(
                    h.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions {
                        PermitLimit = 600,
                        Window = TimeSpan.FromMinutes(1),
                        AutoReplenishment = true,
                        QueueLimit = 0,
                    }
                )
            );
            o.AddPolicy(
                "identity",
                h =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        h.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions {
                            PermitLimit = 60,
                            Window = TimeSpan.FromMinutes(1),
                            AutoReplenishment = true,
                            QueueLimit = 0,
                        }
                    )
            );
        });
        b.Services.AddScoped<IdentityOperations>();
        b.Services.AddScoped<EmailVerification>();
        b.Services.AddScoped<OrganizationOperations>();
        b.Services.AddSingleton<IYardPermissionProvider, StandardPermissions>();
        b.Services.AddSingleton<ISmtpClientFactory, SmtpClientFactory>();
        b.Services.Configure<SmtpOptions>(b.Configuration.GetSection("Yard:Mail"));
        if (
            b.Environment.IsDevelopment()
            && string.IsNullOrWhiteSpace(b.Configuration["Yard:Mail:Host"])
        )
            b.Services.AddSingleton<IEmailSender, DevelopmentEmailSender>();
        else
            b.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
        b.Services.AddOpenApi();
        b.Services.AddHttpClient();
        var gh = b.Configuration.GetSection("Yard:GitHub");
        if (gh.GetValue<bool>("Enabled")) {
            if (
                string.IsNullOrWhiteSpace(gh["ClientId"])
                || string.IsNullOrWhiteSpace(gh["ClientSecret"])
            )
                throw new InvalidOperationException(
                    "GitHub ClientId and ClientSecret are required when enabled."
                );
            b.Services.AddOpenIddict()
                .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<T>())
                .AddClient(o => {
                    o.AllowAuthorizationCodeFlow();
                    o.SetRedirectionEndpointUris("/api/auth/external/github/callback");
                    var secret = Convert.FromBase64String(settings.VerificationKey);
                    o.AddEncryptionKey(
                        new SymmetricSecurityKey(secret) { KeyId = "yard-state-encryption" }
                    );
                    o.AddSigningKey(
                        new SymmetricSecurityKey(
                            SHA256.HashData(
                                secret.Concat(Encoding.UTF8.GetBytes("signing")).ToArray()
                            )
                        ) {
                            KeyId = "yard-state-signing",
                        }
                    );
                    var asp = o.UseAspNetCore().EnableRedirectionEndpointPassthrough();
                    if (b.Environment.IsDevelopment())
                        asp.DisableTransportSecurityRequirement();
                    o.UseDataProtection();
                    o.UseSystemNetHttp().SetProductInformation("Iugam.Yard", "0.1.0");
                    b.Configuration["Yard:PublicOrigin"] = settings.PublicOrigin;
                    // Test-only loopback provider configuration is handled by the standard client event pipeline.
                    GitHubIdentity.ConfigureTestProvider(o, b.Configuration, b.Environment);
                });
        }
        b.Services.Configure<ForwardedHeadersOptions>(o => {
            o.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            o.KnownProxies.Add(System.Net.IPAddress.Loopback);
            o.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
            o.AllowedHosts.Add(origin.Host);
            foreach (
                var proxy in b.Configuration.GetSection("Yard:TrustedProxies").Get<string[]>() ?? []
            ) {
                if (!System.Net.IPAddress.TryParse(proxy, out var address))
                    throw new InvalidOperationException(
                        "Yard:TrustedProxies must contain IP addresses."
                    );
                o.KnownProxies.Add(address);
            }
        });
        return b;
    }

    public static async Task<bool> RunYardCommandAsync(this WebApplication app, string[] args) {
        if (args.FirstOrDefault() != "yard")
            return false;
        if (args.Skip(1).FirstOrDefault() == "contracts") {
            app.UseRouting();
            app.UseEndpoints(_ => { });
        }
        Environment.ExitCode = await ManagementCommands.Run(app.Services, args.Skip(1).ToArray());
        await app.DisposeAsync();
        return true;
    }

    public static WebApplication UseYard(this WebApplication app) {
        app.Use(
            async (h, next) => {
                try {
                    await next();
                }
                catch (YardHttpException e) {
                    if (h.Response.HasStarted)
                        throw;
                    h.Response.Clear();
                    h.Response.StatusCode = e.Status;
                    await h.Response.WriteAsJsonAsync(
                        new { errorCode = e.Message, traceId = h.TraceIdentifier }
                    );
                }
                catch (DbUpdateException) {
                    if (h.Response.HasStarted)
                        throw;
                    h.Response.Clear();
                    h.Response.StatusCode = 409;
                    await h.Response.WriteAsJsonAsync(
                        new { errorCode = "conflict", traceId = h.TraceIdentifier }
                    );
                }
            }
        );
        app.UseForwardedHeaders();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.Use(
            async (h, next) => {
                if (h.Request.Path.StartsWithSegments("/api")) {
                    h.Response.Headers.CacheControl = "no-store";
                    if (
                        HttpMethods.IsPost(h.Request.Method)
                        || HttpMethods.IsPut(h.Request.Method)
                        || HttpMethods.IsPatch(h.Request.Method)
                        || HttpMethods.IsDelete(h.Request.Method)
                    ) {
                        var origin = h.Request.Headers.Origin.ToString();
                        var configured = h
                            .RequestServices.GetRequiredService<YardSettings>()
                            .PublicOrigin;
                        if (origin.Length > 0 && origin.TrimEnd('/') != configured.TrimEnd('/'))
                            throw new YardHttpException(403, "origin_invalid");
                        try {
                            await h
                                .RequestServices.GetRequiredService<IAntiforgery>()
                                .ValidateRequestAsync(h);
                        }
                        catch (AntiforgeryValidationException) {
                            throw new YardHttpException(400, "csrf_invalid");
                        }
                    }
                    if (h.User.Identity?.IsAuthenticated == true) {
                        var u = await h
                            .RequestServices.GetRequiredService<IdentityOperations>()
                            .Current(h);
                        var allowed = new[]
                        {
                            "/api/auth/me",
                            "/api/auth/csrf",
                            "/api/auth/logout",
                            "/api/auth/change-password",
                        };
                        if (u.MustChangePassword && !allowed.Contains(h.Request.Path.Value))
                            throw new YardHttpException(403, "password_change_required");
                    }
                }
                await next();
            }
        );
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
        app.MapGet(
            "/health/ready",
            async (YardDbContext db) => {
                try {
                    var pending = await db.Database.GetPendingMigrationsAsync();
                    return await db.Database.CanConnectAsync() && !pending.Any()
                        ? Results.Ok(new { status = "ready" })
                        : Results.StatusCode(503);
                }
                catch {
                    return Results.StatusCode(503);
                }
            }
        );
        app.MapOpenApi();
        if (app.Services.GetRequiredService<YardSettings>().Primary) {
            IdentityEndpoints.Map(app);
            OrganizationEndpoints.Map(app);
            GitHubIdentity.Map(app);
        }
        if (app.Environment.IsDevelopment())
            app.MapGet(
                    "/api/__yard/mail",
                    async (HttpContext h, YardSettings s, IdentityOperations id) => {
                        await id.Admin(h);
                        var dir = Path.Combine(s.Root, ".yard", "mail");
                        return Results.Ok(
                            Directory.Exists(dir)
                                ? Directory
                                    .GetFiles(dir, "*.json")
                                    .Select(x =>
                                        JsonSerializer.Deserialize<CapturedMail>(
                                            File.ReadAllText(x)
                                        )
                                    )
                                    .OrderByDescending(x => x!.At)
                                    .ToArray()
                                : Array.Empty<CapturedMail>()
                        );
                    }
                )
                .RequireAuthorization();
        return app;
    }
}
