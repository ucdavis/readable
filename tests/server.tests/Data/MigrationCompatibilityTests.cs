using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using server.core.Data;

namespace Server.Tests.Data;

public class MigrationCompatibilityTests
{
    [Fact]
    public void SqlServerModel_MatchesTheLatestMigration()
    {
        // Startup calls MigrateAsync, which rejects pending model changes in EF Core 10.
        // This comparison uses the SQL Server provider without connecting to a database.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=localhost;Database=ReadableMigrationCompatibility;Integrated Security=true",
                sql => sql.MigrationsAssembly("server.core"))
            .Options;
        using var context = new AppDbContext(options);

        context.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
