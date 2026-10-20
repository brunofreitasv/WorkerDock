using WorkerDock.ControlCenter.Registry;

namespace WorkerDock.UnitTests;

public sealed class CentralStorageOptionsTests
{
    [Fact]
    public void ValidateDataDirectory_accepts_absolute_state_path_outside_application()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "WorkerDock", "web");
        var statePath = Path.Combine(Path.GetTempPath(), "WorkerDock-State", Guid.NewGuid().ToString("N"));

        Assert.Equal(Path.GetFullPath(statePath), CentralStorageOptions.ValidateDataDirectory(statePath, contentRoot));
    }

    [Theory]
    [InlineData("relative-state")]
    public void ValidateDataDirectory_rejects_relative_path(string path)
    {
        Assert.Throws<InvalidOperationException>(() => CentralStorageOptions.ValidateDataDirectory(path, Path.GetTempPath()));
    }

    [Fact]
    public void ValidateDataDirectory_rejects_application_root_and_descendants()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "WorkerDock", "web");

        Assert.Throws<InvalidOperationException>(() => CentralStorageOptions.ValidateDataDirectory(contentRoot, contentRoot));
        Assert.Throws<InvalidOperationException>(() => CentralStorageOptions.ValidateDataDirectory(Path.Combine(contentRoot, "data"), contentRoot));
    }

    [Theory]
    [InlineData("wwwroot")]
    [InlineData("releases")]
    public void ValidateDataDirectory_rejects_web_or_release_path_segments(string segment)
    {
        var path = Path.Combine(Path.GetTempPath(), "WorkerDock", segment, "state");

        Assert.Throws<InvalidOperationException>(() => CentralStorageOptions.ValidateDataDirectory(path, Path.Combine(Path.GetTempPath(), "other-app")));
    }
}
