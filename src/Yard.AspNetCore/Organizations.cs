using System.Text.Json;

namespace Yard.AspNetCore;

public sealed record OrganizationBody(string Name);

public sealed record ArchiveBody(bool Archived);

public sealed record TransferBody(Guid UserId, string? Password);

public sealed record RoleBody(string Name, string[] Permissions);

public sealed record AssignRolesBody(Guid[] RoleIds);

public sealed record InvitationBody(string Identifier);

public sealed class OrganizationOperations(
    YardDbContext db,
    OrganizationScope scope,
    IEnumerable<IYardPermissionProvider> providers
) {
    public string[] Catalog =>
        providers.SelectMany(x => x.Permissions).Distinct().Order().ToArray();

    public async Task<Organization> Require(
        HttpContext h,
        Guid id,
        string permission,
        bool allowArchived = false
    ) {
        var actor = IdentityOperations.Actor(h);
        scope.Id = id;
        var org =
            await db.Organizations.SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new YardHttpException(404, "organization_not_found");
        if (!await db.OrganizationMembers.AnyAsync(x => x.UserId == actor))
            throw new YardHttpException(404, "organization_not_found");
        if (org.Archived && !allowArchived)
            throw new YardHttpException(409, "organization_archived");
        if (org.OwnerId != actor) {
            var roles = await (
                from m in db.MemberRoles
                join role in db.OrganizationRoles on m.RoleId equals role.Id
                where m.UserId == actor
                select role.Permissions
            ).ToListAsync();
            if (
                !roles
                    .SelectMany(x => JsonSerializer.Deserialize<string[]>(x)!)
                    .Contains(permission)
            )
                throw new YardHttpException(403, "permission_denied");
        }
        return org;
    }

    public async Task Owner(HttpContext h, Guid id, bool allowArchived = false) {
        var o = await Require(h, id, "organization.read", allowArchived);
        if (o.OwnerId != IdentityOperations.Actor(h))
            throw new YardHttpException(403, "organization_owner_required");
    }

    public void Name(string name) {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            throw new YardHttpException(400, "invalid_name");
    }

    public async Task ValidatePermissions(HttpContext h, Guid id, string[] requested) {
        if (
            requested.Distinct().Count() != requested.Length
            || requested.Any(x => !Catalog.Contains(x))
        )
            throw new YardHttpException(400, "invalid_permission");
        foreach (var p in requested)
            await Require(h, id, p);
    }
}

public static class OrganizationEndpoints {
    public static void Map(WebApplication app) {
        var g = app.MapGroup("/api/organizations")
            .RequireAuthorization()
            .AddEndpointFilter<BodyValidation>();
        g.MapGet(
            "/",
            async (HttpContext h, YardDbContext db) => {
                var uid = IdentityOperations.Actor(h);
                return Results.Ok(
                    await (
                        from m in db.OrganizationMembers.IgnoreQueryFilters()
                        join o in db.Organizations on m.OrganizationId equals o.Id
                        where m.UserId == uid
                        select new {
                            o.Id,
                            o.Name,
                            o.OwnerId,
                            o.Archived,
                        }
                    ).ToListAsync()
                );
            }
        );
        g.MapPost(
            "/",
            async (
                OrganizationBody b,
                HttpContext h,
                YardDbContext db,
                OrganizationOperations ops,
                OrganizationScope scope,
                IdentityOperations i
            ) => {
                ops.Name(b.Name);
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                var uid = IdentityOperations.Actor(h);
                var o = new Organization { Name = b.Name.Trim(), OwnerId = uid };
                scope.Id = o.Id;
                db.Organizations.Add(o);
                db.OrganizationMembers.Add(new() { OrganizationId = o.Id, UserId = uid });
                db.OrganizationRoles.AddRange(
                    new OrganizationRole {
                        OrganizationId = o.Id,
                        Name = "Administrator",
                        Permissions = System.Text.Json.JsonSerializer.Serialize(ops.Catalog),
                    },
                    new OrganizationRole {
                        OrganizationId = o.Id,
                        Name = "Member",
                        Permissions = "[\"organization.read\",\"members.read\",\"roles.read\"]",
                    }
                );
                i.Audit("organization.created", o.Id.ToString(), uid, o.Id);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Results.Ok(o);
            }
        );
        g.MapGet(
            "/{id:guid}",
            async (Guid id, HttpContext h, OrganizationOperations ops) =>
                Results.Ok(await ops.Require(h, id, "organization.read", true))
        );
        g.MapPatch(
            "/{id:guid}",
            async (
                Guid id,
                OrganizationBody b,
                HttpContext h,
                YardDbContext db,
                OrganizationOperations ops,
                IdentityOperations i
            ) => {
                ops.Name(b.Name);
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                var o = await ops.Require(h, id, "organization.update");
                o.Name = b.Name.Trim();
                o.Version++;
                i.Audit("organization.updated", id.ToString(), IdentityOperations.Actor(h), id);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Results.Ok(o);
            }
        );
        g.MapPatch(
            "/{id:guid}/archive",
            async (
                Guid id,
                ArchiveBody b,
                HttpContext h,
                YardDbContext db,
                OrganizationOperations ops,
                IdentityOperations i
            ) => {
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                await ops.Owner(h, id, true);
                var o = await db.Organizations.SingleAsync(x => x.Id == id);
                o.Archived = b.Archived;
                o.Version++;
                i.Audit(
                    "organization.archive",
                    b.Archived.ToString(),
                    IdentityOperations.Actor(h),
                    id
                );
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Results.NoContent();
            }
        );
        g.MapPost(
            "/{id:guid}/transfer",
            async (
                Guid id,
                TransferBody b,
                HttpContext h,
                YardDbContext db,
                OrganizationOperations ops,
                IdentityOperations i
            ) => {
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                await ops.Owner(h, id);
                await i.VerifyPasswordOrProof(h, b.Password);
                if (
                    !await db.OrganizationMembers.AnyAsync(x => x.UserId == b.UserId)
                    || !await db.Users.AnyAsync(x => x.Id == b.UserId && x.IsEnabled)
                )
                    throw new YardHttpException(400, "active_member_required");
                var o = await db.Organizations.SingleAsync(x => x.Id == id);
                if (
                    o.OwnerId != b.UserId
                    && !await db.MemberRoles.AnyAsync(x => x.UserId == o.OwnerId)
                ) {
                    var memberRole = await db.OrganizationRoles.SingleOrDefaultAsync(x =>
                        x.Name == "Member"
                    );
                    if (memberRole != null)
                        db.MemberRoles.Add(
                            new() {
                                OrganizationId = id,
                                UserId = o.OwnerId,
                                RoleId = memberRole.Id,
                            }
                        );
                }
                o.OwnerId = b.UserId;
                o.Version++;
                i.Audit(
                    "organization.ownership-transferred",
                    b.UserId.ToString(),
                    IdentityOperations.Actor(h),
                    id
                );
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Results.NoContent();
            }
        );
        g.MapGet(
            "/{id:guid}/members",
            async (Guid id, HttpContext h, YardDbContext db, OrganizationOperations ops) => {
                await ops.Require(h, id, "members.read");
                var members = await (
                    from m in db.OrganizationMembers
                    join u in db.Users on m.UserId equals u.Id
                    select new {
                        u.Id,
                        Username = u.UserName,
                        u.DisplayName,
                        u.IsEnabled,
                    }
                ).ToListAsync();
                var assignments = await db.MemberRoles.ToListAsync();
                return Results.Ok(
                    members.Select(x => new {
                        x.Id,
                        x.Username,
                        x.DisplayName,
                        x.IsEnabled,
                        RoleIds = assignments.Where(a => a.UserId == x.Id).Select(a => a.RoleId),
                    })
                );
            }
        );
        g.MapDelete(
            "/{id:guid}/members/{uid:guid}",
            async (
                Guid id,
                Guid uid,
                HttpContext h,
                YardDbContext db,
                OrganizationOperations ops,
                IdentityOperations i
            ) => {
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                var o = await ops.Require(h, id, "members.manage");
                if (uid == o.OwnerId)
                    throw new YardHttpException(409, "owner_protected");
                var m =
                    await db.OrganizationMembers.SingleOrDefaultAsync(x => x.UserId == uid)
                    ?? throw new YardHttpException(404, "member_not_found");
                db.OrganizationMembers.Remove(m);
                db.MemberRoles.RemoveRange(
                    await db.MemberRoles.Where(x => x.UserId == uid).ToListAsync()
                );
                i.Audit(
                    "organization.member.removed",
                    uid.ToString(),
                    IdentityOperations.Actor(h),
                    id
                );
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Results.NoContent();
            }
        );
        g.MapGet(
            "/{id:guid}/permissions",
            async (Guid id, HttpContext h, OrganizationOperations ops) => {
                await ops.Require(h, id, "roles.read");
                return Results.Ok(ops.Catalog);
            }
        );
        g.MapGet(
            "/{id:guid}/roles",
            async (Guid id, HttpContext h, YardDbContext db, OrganizationOperations ops) => {
                await ops.Require(h, id, "roles.read");
                return Results.Ok(
                    (await db.OrganizationRoles.ToListAsync()).Select(x => new {
                        x.Id,
                        x.Name,
                        Permissions = JsonSerializer.Deserialize<string[]>(x.Permissions),
                    })
                );
            }
        );
        g.MapPost(
            "/{id:guid}/roles",
            async (
                Guid id,
                RoleBody b,
                HttpContext h,
                YardDbContext db,
                OrganizationOperations ops,
                IdentityOperations i
            ) => {
                ops.Name(b.Name);
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                await ops.Require(h, id, "roles.manage");
                await ops.ValidatePermissions(h, id, b.Permissions);
                var role = new OrganizationRole {
                    OrganizationId = id,
                    Name = b.Name.Trim(),
                    Permissions = JsonSerializer.Serialize(b.Permissions),
                };
                db.OrganizationRoles.Add(role);
                i.Audit(
                    "organization.role.created",
                    role.Id.ToString(),
                    IdentityOperations.Actor(h),
                    id
                );
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Results.Ok(role);
            }
        );
        g.MapPut(
            "/{id:guid}/roles/{rid:guid}",
            async (
                Guid id,
                Guid rid,
                RoleBody b,
                HttpContext h,
                YardDbContext db,
                OrganizationOperations ops,
                IdentityOperations i
            ) => {
                ops.Name(b.Name);
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                await ops.Require(h, id, "roles.manage");
                await ops.ValidatePermissions(h, id, b.Permissions);
                var role =
                    await db.OrganizationRoles.SingleOrDefaultAsync(x => x.Id == rid)
                    ?? throw new YardHttpException(404, "role_not_found");
                role.Name = b.Name.Trim();
                role.Permissions = JsonSerializer.Serialize(b.Permissions);
                i.Audit(
                    "organization.role.updated",
                    rid.ToString(),
                    IdentityOperations.Actor(h),
                    id
                );
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Results.NoContent();
            }
        );
        g.MapDelete(
            "/{id:guid}/roles/{rid:guid}",
            async (
                Guid id,
                Guid rid,
                HttpContext h,
                YardDbContext db,
                OrganizationOperations ops,
                IdentityOperations i
            ) => {
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                await ops.Require(h, id, "roles.manage");
                var role =
                    await db.OrganizationRoles.SingleOrDefaultAsync(x => x.Id == rid)
                    ?? throw new YardHttpException(404, "role_not_found");
                db.MemberRoles.RemoveRange(
                    await db.MemberRoles.Where(x => x.RoleId == rid).ToListAsync()
                );
                db.OrganizationRoles.Remove(role);
                i.Audit(
                    "organization.role.deleted",
                    rid.ToString(),
                    IdentityOperations.Actor(h),
                    id
                );
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Results.NoContent();
            }
        );
        g.MapPut(
            "/{id:guid}/members/{uid:guid}/roles",
            async (
                Guid id,
                Guid uid,
                AssignRolesBody b,
                HttpContext h,
                YardDbContext db,
                OrganizationOperations ops,
                IdentityOperations i
            ) => {
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                var o = await ops.Require(h, id, "roles.manage");
                if (!await db.OrganizationMembers.AnyAsync(x => x.UserId == uid))
                    throw new YardHttpException(404, "member_not_found");
                var roles = await db
                    .OrganizationRoles.Where(x => b.RoleIds.Contains(x.Id))
                    .ToListAsync();
                if (roles.Count != b.RoleIds.Distinct().Count())
                    throw new YardHttpException(400, "role_not_in_organization");
                foreach (var role in roles)
                    await ops.ValidatePermissions(
                        h,
                        id,
                        JsonSerializer.Deserialize<string[]>(role.Permissions)!
                    );
                db.MemberRoles.RemoveRange(
                    await db.MemberRoles.Where(x => x.UserId == uid).ToListAsync()
                );
                await db.SaveChangesAsync();
                db.MemberRoles.AddRange(
                    roles.Select(x => new MemberRole {
                        OrganizationId = id,
                        UserId = uid,
                        RoleId = x.Id,
                    })
                );
                i.Audit(
                    "organization.member.roles",
                    uid.ToString(),
                    IdentityOperations.Actor(h),
                    id
                );
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Results.NoContent();
            }
        );
        g.MapGet(
            "/{id:guid}/invitations",
            async (Guid id, HttpContext h, YardDbContext db, OrganizationOperations ops) => {
                await ops.Require(h, id, "invitations.manage");
                return Results.Ok(await db.OrganizationInvitations.ToListAsync());
            }
        );
        g.MapPost(
            "/{id:guid}/invitations",
            async (
                Guid id,
                InvitationBody b,
                HttpContext h,
                YardDbContext db,
                OrganizationOperations ops,
                IdentityOperations i,
                UserManager<YardUser> users
            ) => {
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                await ops.Require(h, id, "invitations.manage");
                var target = b.Identifier.Contains('@')
                    ? await users.FindByEmailAsync(b.Identifier)
                    : await users.FindByNameAsync(b.Identifier);
                if (
                    target == null
                    || !target.IsEnabled
                    || b.Identifier.Contains('@') && !target.EmailConfirmed
                )
                    throw new YardHttpException(404, "existing_user_required");
                if (await db.OrganizationMembers.AnyAsync(x => x.UserId == target.Id))
                    throw new YardHttpException(409, "already_member");
                var invite = await db.OrganizationInvitations.SingleOrDefaultAsync(x =>
                    x.UserId == target.Id && x.Status == "pending"
                );
                if (invite == null) {
                    invite = new() { OrganizationId = id, UserId = target.Id };
                    db.OrganizationInvitations.Add(invite);
                }
                invite.InviterId = IdentityOperations.Actor(h);
                invite.ExpiresAt = DateTimeOffset.UtcNow.AddDays(7);
                i.Audit(
                    "organization.invitation.sent",
                    target.Id.ToString(),
                    IdentityOperations.Actor(h),
                    id
                );
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Results.Ok(invite);
            }
        );
        g.MapDelete(
            "/{id:guid}/invitations/{iid:guid}",
            async (
                Guid id,
                Guid iid,
                HttpContext h,
                YardDbContext db,
                OrganizationOperations ops,
                IdentityOperations i
            ) => {
                await using var tx = await db.Database.BeginTransactionAsync();
                await i.Lock();
                await ops.Require(h, id, "invitations.manage");
                var invite =
                    await db.OrganizationInvitations.SingleOrDefaultAsync(x => x.Id == iid)
                    ?? throw new YardHttpException(404, "invitation_not_found");
                invite.Status = "revoked";
                i.Audit(
                    "organization.invitation.revoked",
                    iid.ToString(),
                    IdentityOperations.Actor(h),
                    id
                );
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return Results.NoContent();
            }
        );
        g.MapGet(
            "/{id:guid}/audit",
            async (Guid id, HttpContext h, YardDbContext db, OrganizationOperations ops) => {
                await ops.Require(h, id, "audit.read");
                return Results.Ok(
                    await db
                        .IdentityAudits.Where(x => x.OrganizationId == id)
                        .OrderByDescending(x => x.Id)
                        .Take(100)
                        .ToListAsync()
                );
            }
        );
        app.MapGet(
                "/api/invitations",
                async (HttpContext h, YardDbContext db) => {
                    var uid = IdentityOperations.Actor(h);
                    return Results.Ok(
                        await (
                            from i in db.OrganizationInvitations.IgnoreQueryFilters()
                            join o in db.Organizations on i.OrganizationId equals o.Id
                            where i.UserId == uid && i.Status == "pending" && !o.Archived
                            select new {
                                i.Id,
                                i.OrganizationId,
                                OrganizationName = o.Name,
                                i.ExpiresAt,
                            }
                        ).ToListAsync()
                    );
                }
            )
            .RequireAuthorization();
        app.MapPost(
                "/api/invitations/{iid:guid}/accept",
                async (
                    Guid iid,
                    HttpContext h,
                    YardDbContext db,
                    OrganizationScope scope,
                    IdentityOperations i
                ) => {
                    await using var tx = await db.Database.BeginTransactionAsync();
                    await i.Lock();
                    var uid = IdentityOperations.Actor(h);
                    var invite =
                        await db
                            .OrganizationInvitations.IgnoreQueryFilters()
                            .SingleOrDefaultAsync(x => x.Id == iid && x.UserId == uid)
                        ?? throw new YardHttpException(404, "invitation_not_found");
                    scope.Id = invite.OrganizationId;
                    if (invite.Status == "accepted") {
                        await tx.CommitAsync();
                        return Results.NoContent();
                    }
                    if (
                        invite.Status != "pending"
                        || invite.ExpiresAt <= DateTimeOffset.UtcNow
                        || await db.Organizations.AnyAsync(x =>
                            x.Id == invite.OrganizationId && x.Archived
                        )
                    )
                        throw new YardHttpException(409, "invitation_unavailable");
                    if (!await db.OrganizationMembers.AnyAsync(x => x.UserId == uid)) {
                        db.OrganizationMembers.Add(
                            new() { OrganizationId = invite.OrganizationId, UserId = uid }
                        );
                        var role = await db.OrganizationRoles.SingleOrDefaultAsync(x =>
                            x.Name == "Member"
                        );
                        if (role != null)
                            db.MemberRoles.Add(
                                new() {
                                    OrganizationId = invite.OrganizationId,
                                    UserId = uid,
                                    RoleId = role.Id,
                                }
                            );
                    }
                    invite.Status = "accepted";
                    i.Audit(
                        "organization.invitation.accepted",
                        iid.ToString(),
                        uid,
                        invite.OrganizationId
                    );
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                    return Results.NoContent();
                }
            )
            .RequireAuthorization();
    }
}
