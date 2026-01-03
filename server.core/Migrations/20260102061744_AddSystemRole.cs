using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace server.core.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Insert the "System" role if it does not already exist
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM [Roles] WHERE LOWER([Name]) = N'system')
BEGIN
    INSERT INTO [Roles] ([Name]) VALUES (N'System');
END
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove the "System" role only if no users are assigned to it
            migrationBuilder.Sql(@"
DECLARE @roleId int;
SELECT @roleId = [RoleId] FROM [Roles] WHERE LOWER([Name]) = N'system';

IF @roleId IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM [UserRoles] WHERE [RoleId] = @roleId)
    BEGIN
        DELETE FROM [Roles] WHERE [RoleId] = @roleId;
    END
END
");
        }
    }
}

