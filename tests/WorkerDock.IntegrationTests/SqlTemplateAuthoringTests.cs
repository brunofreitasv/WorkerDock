using System.Data;
using System.Text.Json;
using WorkerDock.Contracts.Templates;
using WorkerDock.Contracts.Values;
using WorkerDock.Infrastructure.Templates;
using WorkerDock.Templates.SqlServer;

namespace WorkerDock.IntegrationTests;

public sealed class SqlTemplateAuthoringTests
{
    [Fact]
    public void Sql_template_versions_retain_legacy_digest_and_annotate_only_v11_values()
    {
        var old = SqlTemplateDescriptors.Get("1.0.0");
        Assert.Equal("B9E119AAD34E89E0A5A4332F97A757017C2F1D1C208CF5D0FAE4E65B0902717F", old.MetadataDigest);
        Assert.Equal("1.0.0", new SqlTemplate().Descriptor.Version);
        Assert.Equal("1.1.0", new SqlTemplate("1.1.0").Descriptor.Version);
        Assert.False(old.ConfigurationSchema.GetProperty("properties").GetProperty("parameters").GetProperty("items").GetProperty("properties").GetProperty("value").TryGetProperty("x-workerDock-dynamicValue", out _));
        Assert.True(SqlTemplateDescriptors.Get("1.1.0").ConfigurationSchema.GetProperty("properties").GetProperty("parameters").GetProperty("items").GetProperty("properties").GetProperty("value").GetProperty("x-workerDock-dynamicValue").GetBoolean());
    }

    [Fact]
    public void Procedure_text_requires_identifier_and_dynamic_binding_is_the_value_source()
    {
        var descriptor = SqlTemplateDescriptors.Get("1.1.0");
        var config = Base("Exec dbo.Job @Dt_Ref = @Dt_Ref") with
        {
            Parameters = [new("@Dt_Ref", SqlDbType.Date, null, null, false, false, null, null, null)]
        };
        var plan = new DynamicValuePlan(1, "UTC", [new("/parameters/0/value", DynamicValueSource.ReferenceDate, null, 0, 0, DynamicValueBoundary.None, DynamicValueFormat.IsoDate)]);
        var result = BuiltInAuthoringValidators.Create().Validate(new(descriptor, JsonSerializer.SerializeToElement(config, SqlJobConfiguration.JsonOptions), plan, null, "Manual"));
        Assert.Contains(result, issue => issue.Code == "SqlProcedureNameRequired" && issue.Path == "configuration.commandText");
        Assert.DoesNotContain(result, issue => issue.Code is "DynamicTypeMismatch" or "SqlValueSourceRequired");
        var validConfig = config with { CommandText = "dbo.Job" };
        var valid = BuiltInAuthoringValidators.Create().Validate(new(descriptor, JsonSerializer.SerializeToElement(validConfig, SqlJobConfiguration.JsonOptions), plan, null, "Manual"));
        Assert.DoesNotContain(valid, issue => issue.Path == "configuration.commandText");
        Assert.DoesNotContain(valid, issue => issue.Code is "SqlValueSourceRequired" or "DynamicTypeMismatch");
    }

    [Fact]
    public void Decimal_validation_explains_scale_and_size_metadata_rules()
    {
        var descriptor = SqlTemplateDescriptors.Get("1.1.0");
        var config = Base("dbo.Job") with
        {
            CommandKind = "Procedure",
            Parameters = [new("@n", SqlDbType.Decimal, JsonSerializer.SerializeToElement("12.3"), null, false, false, null, 4, 5)]
        };
        var result = BuiltInAuthoringValidators.Create().Validate(new(descriptor, JsonSerializer.SerializeToElement(config, SqlJobConfiguration.JsonOptions), null, null, "Manual"));
        Assert.Contains(result, issue => issue.Code == "SqlScaleExceedsPrecision" && issue.Path == "configuration.parameters[0].scale");
    }

    [Fact]
    public void Missing_and_conflicting_parameter_sources_are_reported_at_value_control()
    {
        var descriptor = SqlTemplateDescriptors.Get("1.1.0");
        var config = Base("dbo.Job") with { Parameters = [new("@n", SqlDbType.Int, null, null, false, false, null, null, null)] };
        var result = BuiltInAuthoringValidators.Create().Validate(new(descriptor, JsonSerializer.SerializeToElement(config, SqlJobConfiguration.JsonOptions), null, null, "Manual"));
        Assert.Contains(result, issue => issue.Code == "SqlValueSourceRequired" && issue.Path == "configuration.parameters[0].value");
        var conflicting = config with { Parameters = [new("@n", SqlDbType.Int, JsonSerializer.SerializeToElement("1"), null, false, false, null, null, null)] };
        var conflict = BuiltInAuthoringValidators.Create().Validate(new(descriptor, JsonSerializer.SerializeToElement(conflicting, SqlJobConfiguration.JsonOptions), new(1, "UTC", [new("/parameters/0/value", DynamicValueSource.ExecutionId, null, 0, 0, DynamicValueBoundary.None, DynamicValueFormat.String)]), null, "Manual"));
        Assert.Contains(conflict, issue => issue.Code == "SqlValueSourceConflict" && issue.Path == "configuration.parameters[0].value");
    }

    [Fact]
    public async Task Versioned_date_conversion_is_wall_clock_deterministic_and_legacy_overload_remains()
    {
        var config = Base("SELECT @value") with { CommandKind = "Query", Parameters = [new("@value", SqlDbType.Date, JsonSerializer.SerializeToElement("2026-10-20"), null, false, false, null, null, null)] };
        using var connection = new Microsoft.Data.SqlClient.SqlConnection();
        using var command = await SqlTemplate.BuildCommandAsync(config, connection, new SqlTemplateTests.NoVault(), "key", null, default, "1.1.0");
        var value = Assert.IsType<DateTime>(command.Parameters["@value"].Value);
        Assert.Equal(DateTimeKind.Unspecified, value.Kind);
        Assert.Equal(new DateTime(2026, 10, 20), value);
        using var noDeadline = await SqlTemplate.BuildCommandAsync(config, connection, new SqlTemplateTests.NoVault(), "key", null, default);
        Assert.Equal(0, noDeadline.CommandTimeout);
        using var legacy = await SqlTemplate.BuildCommandAsync(config, connection, new SqlTemplateTests.NoVault(), "key", null, default);
        Assert.Equal(command.CommandText, legacy.CommandText);
    }

    [Fact]
    public async Task Versioned_datetime_conversion_keeps_explicit_offset_wall_clock()
    {
        var config = Base("SELECT @value") with { CommandKind = "Query", Parameters = [new("@value", SqlDbType.DateTime2, JsonSerializer.SerializeToElement("2026-10-20T09:30:00+02:00"), null, false, false, null, null, null)] };
        using var connection = new Microsoft.Data.SqlClient.SqlConnection();
        using var command = await SqlTemplate.BuildCommandAsync(config, connection, new SqlTemplateTests.NoVault(), "key", null, default, "1.1.0");
        var value = Assert.IsType<DateTime>(command.Parameters["@value"].Value);
        Assert.Equal(DateTimeKind.Unspecified, value.Kind);
        Assert.Equal(new DateTime(2026, 10, 20, 9, 30, 0), value);
    }

    private static SqlJobConfiguration Base(string commandText) => new(Guid.NewGuid(), "catalog", "Procedure", commandText, [], null, false);
}
