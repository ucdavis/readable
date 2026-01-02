using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using server.core.Domain;
using Server.Services;
using Server.Tests;

namespace server.tests.Services;

public class UserServiceTests
{
    [Fact]
    public async Task SyncUserAsync_inserts_user_and_updates_profile_fields()
    {
        // Arrange
        using var ctx = TestDbContextFactory.CreateInMemory();
        var service = new UserService(NullLogger<UserService>.Instance, ctx);

        var oid = Guid.NewGuid();
        var principal1 = CreatePrincipal(oid, name: "Alice Example", email: "alice@example.com");

        // Act
        await service.SyncUserAsync(principal1);

        // Assert
        var inserted = ctx.Users.Single(u => u.EntraObjectId == oid);
        inserted.DisplayName.Should().Be("Alice Example");
        inserted.Email.Should().Be("alice@example.com");

        // Act (update)
        var principal2 = CreatePrincipal(oid, name: "Alice Updated", email: "alice2@example.com");
        await service.SyncUserAsync(principal2);

        // Assert (updated)
        var updated = ctx.Users.Single(u => u.EntraObjectId == oid);
        updated.DisplayName.Should().Be("Alice Updated");
        updated.Email.Should().Be("alice2@example.com");
    }

    [Fact]
    public async Task GetRolesForUser_returns_assigned_role_names()
    {
        // Arrange
        using var ctx = TestDbContextFactory.CreateInMemory();
        var service = new UserService(NullLogger<UserService>.Instance, ctx);

        var oid = Guid.NewGuid();
        var user = new User { EntraObjectId = oid };
        var admin = new Role { Name = "Admin" };
        var editor = new Role { Name = "Editor" };

        ctx.Users.Add(user);
        ctx.Roles.AddRange(admin, editor);
        await ctx.SaveChangesAsync();

        ctx.UserRoles.Add(new UserRole { UserId = user.UserId, RoleId = admin.RoleId });
        ctx.UserRoles.Add(new UserRole { UserId = user.UserId, RoleId = editor.RoleId });
        await ctx.SaveChangesAsync();

        var principal = CreatePrincipal(oid);

        // Act
        var roles = await service.GetRolesForUser(principal);

        // Assert
        roles.Should().Equal(["Admin", "Editor"]);
    }

    [Fact]
    public async Task UpdateUserPrincipalIfNeeded_replaces_role_claims_from_database()
    {
        // Arrange
        using var ctx = TestDbContextFactory.CreateInMemory();
        var service = new UserService(NullLogger<UserService>.Instance, ctx);

        var oid = Guid.NewGuid();
        var user = new User { EntraObjectId = oid };
        var admin = new Role { Name = "Admin" };

        ctx.Users.Add(user);
        ctx.Roles.Add(admin);
        await ctx.SaveChangesAsync();

        ctx.UserRoles.Add(new UserRole { UserId = user.UserId, RoleId = admin.RoleId });
        await ctx.SaveChangesAsync();

        var principal = CreatePrincipal(oid, roles: ["OldRole"]);

        // Act
        var updated = await service.UpdateUserPrincipalIfNeeded(principal);

        // Assert
        updated.Should().NotBeNull();
        updated!.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().Equal(["Admin"]);
    }

    [Fact]
    public async Task UpdateUserPrincipalIfNeeded_returns_null_when_roles_already_match()
    {
        // Arrange
        using var ctx = TestDbContextFactory.CreateInMemory();
        var service = new UserService(NullLogger<UserService>.Instance, ctx);

        var oid = Guid.NewGuid();
        var user = new User { EntraObjectId = oid };
        var admin = new Role { Name = "Admin" };

        ctx.Users.Add(user);
        ctx.Roles.Add(admin);
        await ctx.SaveChangesAsync();

        ctx.UserRoles.Add(new UserRole { UserId = user.UserId, RoleId = admin.RoleId });
        await ctx.SaveChangesAsync();

        var principal = CreatePrincipal(oid, roles: ["Admin"]);

        // Act
        var updated = await service.UpdateUserPrincipalIfNeeded(principal);

        // Assert
        updated.Should().BeNull();
    }

    private static ClaimsPrincipal CreatePrincipal(Guid objectId, string? name = null, string? email = null, IEnumerable<string>? roles = null)
    {
        List<Claim> claims =
        [
            // Azure AD / Entra object id (OID)
            new("oid", objectId.ToString()),
        ];

        if (!string.IsNullOrWhiteSpace(name))
        {
            claims.Add(new Claim("name", name));
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            claims.Add(new Claim("preferred_username", email));
        }

        if (roles is not null)
        {
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        }

        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }
}

