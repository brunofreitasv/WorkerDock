namespace WorkerDock.CentralImport;

public sealed record ImportCommandOptions(string DataDirectory, Guid InstanceId)
{
    public static ImportCommandOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || args[0] != "import")
            throw new ArgumentException("Use the import subcommand.", nameof(args));

        string? dataDirectory = null;
        Guid? instanceId = null;
        for (var index = 1; index < args.Count; index++)
        {
            if (index + 1 >= args.Count) throw new ArgumentException("An option value is missing.", nameof(args));
            var option = args[index++];
            var value = args[index];
            switch (option)
            {
                case "--data-directory" when dataDirectory is null:
                    dataDirectory = value;
                    break;
                case "--instance-id" when instanceId is null:
                    if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
                        throw new ArgumentException("Instance ID is invalid.", nameof(args));
                    instanceId = parsed;
                    break;
                case "--data-directory" or "--instance-id":
                    throw new ArgumentException("An option was specified more than once.", nameof(args));
                default:
                    throw new ArgumentException("An option is not supported.", nameof(args));
            }
        }

        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory) || instanceId is null)
            throw new ArgumentException("An absolute --data-directory and a nonempty --instance-id are required.", nameof(args));

        return new ImportCommandOptions(dataDirectory, instanceId.Value);
    }
}
