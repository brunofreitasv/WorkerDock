using WorkerDock.Application.Common;

namespace WorkerDock.DatabaseMigrator;

public static class SqlServerPrerequisites
{
    public static void Validate(int majorVersion, int compatibilityLevel)
    {
        if (majorVersion < 16) throw new WorkerDockException("UnsupportedSqlServerVersion");
        if (compatibilityLevel != 160) throw new WorkerDockException("UnsupportedCompatibilityLevel");
    }
}
