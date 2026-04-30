using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using opendataloader.api;

var builder = Host.CreateApplicationBuilder(args);
var options = OpenDataLoaderOptions.FromConfiguration(builder.Configuration);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IOpenDataLoaderRunner, OpenDataLoaderRunner>();
builder.Services.AddSingleton<IRuntimeDependencyProbe, RuntimeDependencyProbe>();
builder.Services.AddHostedService<OpenDataLoaderWorker>();

var app = builder.Build();
app.Run();

public partial class Program;
