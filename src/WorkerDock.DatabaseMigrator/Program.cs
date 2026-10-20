using WorkerDock.DatabaseMigrator;

if (args.Length != 3 || args[0] is not ("apply" or "rollback") ||
    !Guid.TryParse(Environment.GetEnvironmentVariable("WORKERDOCK_INSTANCE_ID"), out var instanceId) ||
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WORKERDOCK_SQL")))
{
    Console.Error.WriteLine("Usage: apply|rollback tenant <migration>; set WORKERDOCK_SQL and WORKERDOCK_INSTANCE_ID.");
    return 2;
}

try
{
    var runner = new MigrationRunner(Environment.GetEnvironmentVariable("WORKERDOCK_SQL")!, instanceId,
        Path.Combine(AppContext.BaseDirectory, "database"));
    if (args[0] == "apply") await runner.ApplyAsync(args[1], args[2], default);
    else await runner.RollbackAsync(args[1], args[2], default);
    return 0;
}
catch (WorkerDock.Application.Common.WorkerDockException error)
{
    Console.Error.WriteLine(error.Code);
    return 1;
}
