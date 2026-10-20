using WorkerDock.CentralImport;

namespace WorkerDock.UnitTests;

public sealed class CentralImportCommandTests
{
    [Fact]
    public void Parses_documented_import_command_and_identity()
    {
        var instanceId = Guid.NewGuid();

        var dataDirectory = Path.Combine(Path.GetTempPath(), "WorkerDock", "state");
        var options = ImportCommandOptions.Parse(["import", "--data-directory", dataDirectory, "--instance-id", instanceId.ToString("D")]);

        Assert.Equal(dataDirectory, options.DataDirectory);
        Assert.Equal(instanceId, options.InstanceId);
    }

    public static IEnumerable<object[]> InvalidCommands =>
    [
        [new[] { "--data-directory", "C:\\state", "--instance-id", "11111111-1111-1111-1111-111111111111" }],
        [new[] { "import", "--data-directory", "C:\\state" }],
        [new[] { "import", "--data-directory", "C:\\state", "--instance-id", "not-a-guid" }],
        [new[] { "import", "--unknown", "value" }]
    ];

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public void Rejects_undocumented_or_incomplete_command(string[] args)
    {
        Assert.Throws<ArgumentException>(() => ImportCommandOptions.Parse(args));
    }
}
