using WorkerDock.Application.Common;
using WorkerDock.Infrastructure.Migrations;

namespace WorkerDock.IntegrationTests;

public sealed class MigrationPrerequisiteTests
{
    [Fact]
    public void Sql_server_2019_is_rejected()
    {
        var error = Assert.Throws<WorkerDockException>(() => SqlServerPrerequisites.Validate(15, 160));
        Assert.Equal("UnsupportedSqlServerVersion", error.Code);
    }

    [Fact]
    public void Compatibility_below_160_is_rejected()
    {
        var error = Assert.Throws<WorkerDockException>(() => SqlServerPrerequisites.Validate(16, 150));
        Assert.Equal("UnsupportedCompatibilityLevel", error.Code);
    }

    [Fact]
    public void Sql_server_2022_compatibility_160_is_accepted() => SqlServerPrerequisites.Validate(16, 160);
}
