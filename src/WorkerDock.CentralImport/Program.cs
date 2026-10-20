using System.Text.Json;
using WorkerDock.CentralImport;
using WorkerDock.ControlCenter.Registry;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    try
    {
        var options = ImportCommandOptions.Parse(args);
        var connectionString = Environment.GetEnvironmentVariable("WORKERDOCK_LEGACY_CENTRAL_SQL");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("WORKERDOCK_LEGACY_CENTRAL_SQL is required.");
        var reader = new LegacyCenterReader(connectionString);
        var legacy = await reader.ReadAsync(options.InstanceId, CancellationToken.None);
        var receipt = await CentralJournalBootstrap.ImportAsync(options.DataDirectory, legacy.InstanceId,
            legacy.Registrations, legacy.Audit, legacy.Health, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Import failed ({error.GetType().Name}). No import receipt was produced.");
        return 1;
    }
}
