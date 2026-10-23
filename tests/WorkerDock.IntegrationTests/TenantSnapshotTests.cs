using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using WorkerDock.Infrastructure.Migrations;

namespace WorkerDock.IntegrationTests;

public sealed class TenantSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_matches_full_migration_chain(bool readCommittedSnapshot)
    {
        await using var migrated = new SqlDatabaseFixture();
        await using var snapshot = new SqlDatabaseFixture();
        await migrated.CreateAsync(readCommittedSnapshot);
        await snapshot.CreateAsync(readCommittedSnapshot);
        await ApplyChainAsync(migrated);
        await ExecuteSnapshotAsync(snapshot, migrated.InstanceId);

        AssertEquivalent(await ReadStateAsync(migrated), await ReadStateAsync(snapshot));
        // The baseline must also be accepted by the incremental runner without mutation.
        var before = await ReadStateAsync(snapshot);
        await new MigrationRunner(snapshot.ConnectionString, migrated.InstanceId, snapshot.ScriptsRoot)
            .ApplyAsync("tenant", await LastMigrationAsync(snapshot), CancellationToken.None);
        AssertEquivalent(before, await ReadStateAsync(snapshot));
        Assert.Equal(0, await snapshot.ScalarAsync("SELECT COUNT(*) FROM workerdock.TemplateCatalog WHERE TemplateVersion=N'1.0.0' AND TemplateId IN(N'workerdock.http',N'workerdock.sql-server')"));
    }

    [Theory]
    [InlineData("unrendered", 51070)]
    [InlineData("malformed", 51070)]
    [InlineData("empty-guid", 51070)]
    [InlineData("overlong-guid", 51070)]
    [InlineData("table", 51071)]
    [InlineData("schema", 51071)]
    [InlineData("role", 51071)]
    [InlineData("existing-tenant", 51071)]
    public async Task Unrendered_or_nonempty_snapshot_fails_before_creating_objects(string scenario, int expectedNumber)
    {
        await using var database = new SqlDatabaseFixture();
        await database.CreateAsync(false);
        if (scenario == "table") await database.ExecuteAsync("CREATE TABLE dbo.Sentinel(Value int); INSERT dbo.Sentinel VALUES(42);");
        if (scenario == "schema") await database.ExecuteAsync("CREATE SCHEMA ExistingSchema;");
        if (scenario == "role") await database.ExecuteAsync("CREATE ROLE ExistingRole;");
        if (scenario == "existing-tenant") await ApplyChainAsync(database);
        var before = scenario == "existing-tenant" ? await ReadStateAsync(database) : null;
        var sql = await SnapshotSqlAsync(database);
        var replacement = scenario switch
        {
            "unrendered" => "__WORKERDOCK_INSTANCE_ID__",
            "malformed" => "invalid",
            "empty-guid" => Guid.Empty.ToString(),
            "overlong-guid" => database.InstanceId + "-suffix",
            _ => database.InstanceId.ToString()
        };
        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteSqlAsync(database, sql.Replace("__WORKERDOCK_INSTANCE_ID__", replacement, StringComparison.Ordinal)));
        Assert.Equal(expectedNumber, error.Number);
        if (before is not null) AssertEquivalent(before, await ReadStateAsync(database));
        else Assert.Equal(0, await database.ScalarAsync("SELECT COUNT(*) FROM sys.schemas WHERE name=N'workerdock'"));
        if (scenario == "table") Assert.Equal(42, await database.ScalarAsync("SELECT Value FROM dbo.Sentinel"));
    }

    [Theory]
    [InlineData("grant", "permissions", "REVOKE SELECT ON workerdock.TenantSettings FROM WorkerDockRuntime;")]
    [InlineData("trigger", "modules", "ALTER TRIGGER workerdock.AuditEvents_AppendOnly ON workerdock.AuditEvents AFTER UPDATE, DELETE AS BEGIN SET NOCOUNT ON; END;")]
    [InlineData("seed", "data:TenantSettings", "UPDATE workerdock.TenantSettings SET LogRetentionDays=31 WHERE InstanceId=(SELECT InstanceId FROM workerdock.TenantSettings);")]
    [InlineData("history", "data:SchemaHistory", "UPDATE workerdock.SchemaHistory SET UpChecksum=REPLICATE('0',64) WHERE MigrationId=N'0001_initial';")]
    public async Task Snapshot_drift_is_detected(string drift, string section, string mutation)
    {
        await using var migrated = new SqlDatabaseFixture();
        await using var snapshot = new SqlDatabaseFixture();
        await migrated.CreateAsync(false);
        await snapshot.CreateAsync(false);
        await ApplyChainAsync(migrated);
        await ExecuteSnapshotAsync(snapshot, migrated.InstanceId);
        var expected = await ReadStateAsync(migrated);
        AssertEquivalent(expected, await ReadStateAsync(snapshot));
        await snapshot.ExecuteAsync(mutation);
        var actual = await ReadStateAsync(snapshot);
        Assert.False(expected[section].SequenceEqual(actual[section]), $"Undetected {drift} drift in {section}");
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertEquivalent(expected, actual));
    }

    private static async Task<string> LastMigrationAsync(SqlDatabaseFixture database)
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(database.ScriptsRoot, "tenant", "manifest.json")));
        return manifest.RootElement.EnumerateArray().Last().GetProperty("id").GetString()!;
    }

    private static async Task ApplyChainAsync(SqlDatabaseFixture database) =>
        await new MigrationRunner(database.ConnectionString, database.InstanceId, database.ScriptsRoot)
            .ApplyAsync("tenant", await LastMigrationAsync(database), CancellationToken.None);

    private static Task<string> SnapshotSqlAsync(SqlDatabaseFixture database) =>
        File.ReadAllTextAsync(Path.Combine(database.ScriptsRoot, "tenant", "current-state.sql"));

    private static async Task ExecuteSnapshotAsync(SqlDatabaseFixture database, Guid instanceId) =>
        await ExecuteSqlAsync(database, (await SnapshotSqlAsync(database)).Replace("__WORKERDOCK_INSTANCE_ID__", instanceId.ToString(), StringComparison.Ordinal));

    private static async Task ExecuteSqlAsync(SqlDatabaseFixture database, string sql)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        foreach (var batch in Regex.Split(sql, @"(?im)^\s*GO\s*$"))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static void AssertEquivalent(SortedDictionary<string, string[]> expected, SortedDictionary<string, string[]> actual)
    {
        Assert.Equal(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (var section in expected.Keys)
            Assert.True(expected[section].SequenceEqual(actual[section]),
                $"Snapshot parity differs in {section}\nMissing: {string.Join('\n', expected[section].Except(actual[section]))}\nExtra: {string.Join('\n', actual[section].Except(expected[section]))}");
    }

    private static async Task<SortedDictionary<string, string[]>> ReadStateAsync(SqlDatabaseFixture database)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        var state = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (name, query) in CatalogQueries)
            state[name] = await ReadRowsAsync(connection, query, name == "modules");

        // Read every table, including empty operational tables. Only known generated
        // values are omitted; tenant identity, all seed values and both history hashes remain.
        var tables = new List<(string Table, string Columns)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT t.name, STRING_AGG(CONVERT(nvarchar(max),QUOTENAME(c.name)),N',') WITHIN GROUP(ORDER BY c.column_id)
                FROM sys.tables t JOIN sys.columns c ON c.object_id=t.object_id
                WHERE t.schema_id=SCHEMA_ID(N'workerdock') AND c.system_type_id<>189
                AND NOT (t.name=N'SchemaHistory' AND c.name=N'AppliedAtUtc')
                AND NOT (t.name=N'PolicyRevisions' AND c.name=N'CreatedAtUtc')
                AND NOT (t.name=N'IntegrationSettingsRevisions' AND c.name IN(N'RevisionId',N'CreatedAtUtc'))
                GROUP BY t.name
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var (table, columns) in tables)
            state["data:" + table] = await ReadRowsAsync(connection, $"SELECT {columns} FROM [workerdock].[{table.Replace("]", "]]", StringComparison.Ordinal)}]");
        return state;
    }

    private static async Task<string[]> ReadRowsAsync(SqlConnection connection, string sql, bool normalizeModules = false)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                if (normalizeModules && reader.GetName(i) == "definition" && value is string definition)
                {
                    // Ignore CREATE/ALTER, line endings and optional identifier delimiters.
                    // Preserve quoted string contents and comments when normalizing identifiers.
                    var normalized = Regex.Replace(definition.Replace("\r\n", "\n", StringComparison.Ordinal).Trim(),
                        @"\AALTER\s+(TRIGGER|PROCEDURE)\b", "CREATE $1", RegexOptions.IgnoreCase);
                    value = Regex.Replace(normalized,
                        @"'(?:''|[^'])*'|--[^\n]*|/\*[\s\S]*?\*/|\[(?<identifier>[A-Za-z_][A-Za-z0-9_]*)\]",
                        match => match.Groups["identifier"].Success ? match.Groups["identifier"].Value : match.Value);
                }
                values[i] = value;
            }
            rows.Add(JsonSerializer.Serialize(values));
        }
        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    private static readonly (string Name, string Sql)[] CatalogQueries =
    [
        ("schema", "SELECT name, USER_NAME(principal_id) FROM sys.schemas WHERE name=N'workerdock'"),
        ("objects", """
            SELECT o.type, COALESCE(OBJECT_NAME(o.parent_object_id),N''),
                   CASE WHEN d.is_system_named=1 THEN N'<default>:'+COL_NAME(d.parent_object_id,d.parent_column_id) ELSE o.name END
            FROM sys.objects o LEFT JOIN sys.default_constraints d ON d.object_id=o.object_id
            WHERE o.schema_id=SCHEMA_ID(N'workerdock') AND o.is_ms_shipped=0
            """),
        ("columns", """
            SELECT OBJECT_NAME(c.object_id),c.name,c.column_id,SCHEMA_NAME(ty.schema_id),TYPE_NAME(c.user_type_id),
                   c.max_length,c.precision,c.scale,c.collation_name,c.is_nullable,c.is_identity,c.is_computed,c.is_rowguidcol,
                   c.is_sparse,c.is_ansi_padded,c.generated_always_type,cc.definition,cc.is_persisted,
                   CONVERT(nvarchar(100),ic.seed_value),CONVERT(nvarchar(100),ic.increment_value),ic.is_not_for_replication
            FROM sys.columns c JOIN sys.objects o ON o.object_id=c.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id=c.object_id AND ic.column_id=c.column_id
            WHERE o.schema_id=SCHEMA_ID(N'workerdock')
            """),
        ("indexes", """
            SELECT OBJECT_NAME(i.object_id),i.name,i.type,i.is_unique,i.is_primary_key,i.is_unique_constraint,
                   i.is_disabled,i.has_filter,i.filter_definition,i.ignore_dup_key,i.fill_factor,i.allow_row_locks,i.allow_page_locks,
                   c.name,ic.key_ordinal,ic.is_descending_key,ic.is_included_column,ic.partition_ordinal
            FROM sys.indexes i JOIN sys.objects o ON o.object_id=i.object_id
            LEFT JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
            LEFT JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE o.schema_id=SCHEMA_ID(N'workerdock')
            """),
        ("checks", """
            SELECT OBJECT_NAME(parent_object_id),name,definition,is_disabled,is_not_trusted,is_not_for_replication
            FROM sys.check_constraints WHERE schema_id=SCHEMA_ID(N'workerdock')
            """),
        ("defaults", """
            SELECT OBJECT_NAME(parent_object_id),COL_NAME(parent_object_id,parent_column_id),
                   CASE WHEN is_system_named=1 THEN N'<generated>' ELSE name END,definition
            FROM sys.default_constraints WHERE schema_id=SCHEMA_ID(N'workerdock')
            """),
        ("foreign-keys", """
            SELECT OBJECT_NAME(f.parent_object_id),f.name,OBJECT_SCHEMA_NAME(f.referenced_object_id),OBJECT_NAME(f.referenced_object_id),
                   f.delete_referential_action,f.update_referential_action,f.is_disabled,f.is_not_trusted,f.is_not_for_replication,
                   fc.constraint_column_id,COL_NAME(fc.parent_object_id,fc.parent_column_id),COL_NAME(fc.referenced_object_id,fc.referenced_column_id)
            FROM sys.foreign_keys f JOIN sys.foreign_key_columns fc ON fc.constraint_object_id=f.object_id
            WHERE f.schema_id=SCHEMA_ID(N'workerdock')
            """),
        ("modules", """
            SELECT o.name,m.definition,m.uses_ansi_nulls,m.uses_quoted_identifier,m.is_schema_bound,m.uses_database_collation,
                   CASE WHEN m.execute_as_principal_id=-2 THEN N'OWNER' ELSE USER_NAME(m.execute_as_principal_id) END
            FROM sys.sql_modules m JOIN sys.objects o ON o.object_id=m.object_id
            WHERE o.schema_id=SCHEMA_ID(N'workerdock')
            """),
        ("triggers", """
            SELECT t.name,OBJECT_NAME(t.parent_id),t.is_disabled,t.is_not_for_replication,t.is_instead_of_trigger,
                   e.type_desc,e.is_first,e.is_last
            FROM sys.triggers t JOIN sys.objects o ON o.object_id=t.object_id
            JOIN sys.trigger_events e ON e.object_id=t.object_id
            WHERE o.schema_id=SCHEMA_ID(N'workerdock')
            """),
        ("roles", "SELECT name,type,USER_NAME(owning_principal_id) FROM sys.database_principals WHERE name=N'WorkerDockRuntime'"),
        ("role-members", """
            SELECT USER_NAME(role_principal_id),USER_NAME(member_principal_id) FROM sys.database_role_members
            WHERE USER_NAME(role_principal_id)=N'WorkerDockRuntime' OR USER_NAME(member_principal_id)=N'WorkerDockRuntime'
            """),
        ("permissions", """
            SELECT USER_NAME(p.grantee_principal_id),USER_NAME(p.grantor_principal_id),p.state,p.permission_name,p.class_desc,
                   CASE p.class WHEN 1 THEN OBJECT_SCHEMA_NAME(p.major_id)+N'.'+OBJECT_NAME(p.major_id)
                                WHEN 3 THEN SCHEMA_NAME(p.major_id) WHEN 4 THEN USER_NAME(p.major_id) ELSE N'<database>' END,
                   CASE WHEN p.class=1 AND p.minor_id>0 THEN COL_NAME(p.major_id,p.minor_id) ELSE N'' END
            FROM sys.database_permissions p
            WHERE USER_NAME(p.grantee_principal_id)=N'WorkerDockRuntime'
               OR (p.class=1 AND OBJECT_SCHEMA_NAME(p.major_id)=N'workerdock')
               OR (p.class=3 AND SCHEMA_NAME(p.major_id)=N'workerdock')
            """)
    ];
}
