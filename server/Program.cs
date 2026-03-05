using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using server.core.Data;
using server.core.Telemetry;
using server.core.Storage;
using server.Helpers;
using Server.Services;

var builder = WebApplication.CreateBuilder(args);

// setup configuration sources (last one wins)
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvFile(".env", optional: true) // secrets stored here
    .AddEnvFile($".env.{builder.Environment.EnvironmentName}", optional: true) // env-specific secrets
    .AddEnvironmentVariables(); // OS env vars override everything

// setup logging and telemetry
TelemetryHelper.ConfigureLogging(builder.Logging, builder.Configuration);
TelemetryHelper.ConfigureOpenTelemetry(builder.Services, builder.Configuration);

// handy for getting true client IP
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

// Add auth config (entra)
builder.Services.AddAuthenticationServices(builder.Configuration);

builder.Services.AddControllers();

// Add response caching for pages that opt-in
// https://learn.microsoft.com/en-us/aspnet/core/performance/caching/middleware?view=aspnetcore-9.0
builder.Services.AddResponseCaching();

// add scoped services here
builder.Services.AddScoped<IDbInitializer, DbInitializer>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IApiKeyService, ApiKeyService>();
builder.Services.AddSingleton<IFileSasService, AzureBlobFileSasService>();
builder.Services.AddSingleton<IProcessedPdfBlobService, AzureProcessedPdfBlobService>();
builder.Services.AddSingleton<IIncomingBlobUploadService, AzureIncomingBlobUploadService>();
// add auth policies here

// add db context (check secrets first, then config, then default)
var conn = builder.Configuration["DB_CONNECTION"]
            ?? builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(conn))
{
    const string message = "No database connection string configured. Set the DB_CONNECTION environment variable or " +
                           "configure ConnectionStrings:DefaultConnection. For local containers use " +
                           "Server=sql,1433;Database=AppDb;User ID=sa;Password=LocalDev123!;Encrypt=False;TrustServerCertificate=True;.";

    throw new InvalidOperationException(message);
}

builder.Services.AddDbContextPool<AppDbContext>(o => o.UseSqlServer(conn, opt => opt.MigrationsAssembly("server.core")));

builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<AppDbContext>();

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("ApiKey", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "API key authentication. Paste the raw key value.",
        Name = "X-Api-Key",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey
    });

    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "ApiKey"
                }
            },
            Array.Empty<string>()
        }
    });
});

// configure data protection (generated keys for auth and such)
var keysPath = Path.Combine(builder.Environment.ContentRootPath, "..", ".aspnet", "DataProtection-Keys");
Directory.CreateDirectory(keysPath);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

var app = builder.Build();

// do db migrations at startup
using (var scope = app.Services.CreateScope())
{
    var init = scope.ServiceProvider.GetRequiredService<IDbInitializer>();
    var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
    await init.InitializeAsync(env.IsDevelopment());
}

app.UseForwardedHeaders();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseResponseCaching();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

// Swagger: available in all environments, but require authentication outside development
if (!app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/swagger"))
        {
            if (context.User?.Identity?.IsAuthenticated != true)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        await next();
    });
}

app.UseSwagger();
app.UseSwaggerUI();

// enrich every log with request context
app.UseRequestContextLogging();

// app.UseHttpLogging(); // if you want extra logging. It's a little overkill though with the current logging setup

app.MapControllers();

var healthEndpoint = app.MapHealthChecks("/health");

// Cache the health check response for 10 seconds to protect the database from rapid polling.
healthEndpoint.WithMetadata(new ResponseCacheAttribute
{
    Duration = 10,
    Location = ResponseCacheLocation.Any,
    NoStore = false,
});


app.MapFallbackToFile("/index.html");

app.Run();
