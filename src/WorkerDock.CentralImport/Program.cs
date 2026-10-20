using System.Text.Json;
using WorkerDock.CentralImport;
using WorkerDock.ControlCenter.Registry;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    try
    {
        var options = Parse(args);
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

static (string DataDirectory, Guid InstanceId) Parse(string[] args)
{
    string? directory = null;
    Guid? instanceId = null;
    for (var index = 0; index < args.Length; index++)
    {
        if (index + 1 >= args.Length) throw new ArgumentException("An option value is missing.");
        switch (args[index])
        {
            case "--data-directory": directory = args[++index]; break;
            case "--instance-id":
                if (!Guid.TryParse(args[++index], out var parsed) || parsed == Guid.Empty) throw new ArgumentException("Instance ID is invalid.");
                instanceId = parsed;
                break;
            default: throw new ArgumentException("An option is not supported.");
        }
    }
    if (string.IsNullOrWhiteSpace(directory) || instanceId is null) throw new ArgumentException("--data-directory and --instance-id are required.");
    return (directory, instanceId.Value);
}
