using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using server.core.Telemetry;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddLogging(logging =>
    TelemetryHelper.ConfigureLogging(logging, clearProviders: false, addJsonConsole: false));
TelemetryHelper.ConfigureOpenTelemetry(builder.Services, TelemetryHostKind.Worker);

builder.Services.AddApplicationInsightsTelemetryWorkerService();
builder.Services.ConfigureFunctionsApplicationInsights();

builder.Build().Run();
