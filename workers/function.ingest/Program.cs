using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using server.core.Data;
using server.core.Ingest;
using server.core.Telemetry;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddLogging(logging =>
    TelemetryHelper.ConfigureLogging(logging, clearProviders: false, addJsonConsole: false));
TelemetryHelper.ConfigureOpenTelemetry(builder.Services, TelemetryHostKind.Worker);

var conn = builder.Configuration["DB_CONNECTION"]
           ?? builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(conn))
{
    const string message = "No database connection string configured. Set the DB_CONNECTION environment variable or " +
                           "configure ConnectionStrings:DefaultConnection.";
    throw new InvalidOperationException(message);
}

builder.Services.AddPooledDbContextFactory<AppDbContext>(o =>
    o.UseSqlServer(conn, opt => opt.MigrationsAssembly("server.core")));

builder.Services.AddFileIngest();

builder.Services.AddApplicationInsightsTelemetryWorkerService();
builder.Services.ConfigureFunctionsApplicationInsights();

builder.Build().Run();
