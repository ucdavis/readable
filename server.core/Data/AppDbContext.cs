using Microsoft.EntityFrameworkCore;
using server.core.Domain;

namespace server.core.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();

    public DbSet<FileRecord> Files => Set<FileRecord>();
    public DbSet<FileProcessingAttempt> FileProcessingAttempts => Set<FileProcessingAttempt>();
    public DbSet<AccessibilityReport> AccessibilityReports => Set<AccessibilityReport>();

    public DbSet<WeatherForecast> WeatherForecasts => Set<WeatherForecast>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        User.OnModelCreating(modelBuilder);
        Role.OnModelCreating(modelBuilder);
        UserRole.OnModelCreating(modelBuilder);
        FileRecord.OnModelCreating(modelBuilder);
        FileProcessingAttempt.OnModelCreating(modelBuilder);
        AccessibilityReport.OnModelCreating(modelBuilder);

        base.OnModelCreating(modelBuilder);
    }
}
