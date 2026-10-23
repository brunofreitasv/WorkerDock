using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using WorkerDock.Contracts.Secrets;
using WorkerDock.Infrastructure.Secrets;
using WorkerDock.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.TryAddSingleton<ISecretVault>(_ => SecretVaultRegistration.CreateTenantSql(builder.Environment.IsDevelopment()));
builder.Services.AddWindowsService();
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(90));
builder.Services.AddHostedService<TenantRuntimeService>();
await builder.Build().RunAsync();
