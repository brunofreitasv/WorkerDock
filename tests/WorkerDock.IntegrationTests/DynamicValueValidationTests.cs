using System.Text.Json;
using WorkerDock.Contracts.Templates;
using WorkerDock.Contracts.Values;
using WorkerDock.Infrastructure.Values;
using WorkerDock.Infrastructure.Templates;

namespace WorkerDock.IntegrationTests;

public sealed class DynamicValueValidationTests
{
    private static TemplateDescriptor Descriptor(string schema) => new("test", "1.0", "Test", "Test",
        JsonDocument.Parse(schema).RootElement.Clone(), JsonDocument.Parse("{}").RootElement.Clone(), "");
    private static DynamicValueBinding Bind(string pointer, DynamicValueFormat format = DynamicValueFormat.String) =>
        new(pointer, DynamicValueSource.ExecutionId, null, 0, 0, DynamicValueBoundary.None, format);

    [Fact]
    public void only_declared_scalar_targets_are_accepted_and_unknown_keys_are_not_echoed()
    {
        var descriptor = Descriptor("""{"type":"object","properties":{"parameters":{"type":"array","items":{"type":"object","properties":{"value":{"type":"string","x-workerDock-dynamicValue":true}}}}}}""");
        var config = JsonDocument.Parse("""{"parameters":[{"value":"fixed"}]}""").RootElement;
        var plan = new DynamicValuePlan(1, "UTC", [Bind("/parameters/0/value")]);
        Assert.Empty(DynamicValueValidation.Validate(descriptor, config, plan, "Scheduled"));
        var bad = plan with { Bindings = [Bind("/untrusted-secret-name")] };
        var issues = DynamicValueValidation.Validate(descriptor, config, bad, "Scheduled");
        Assert.Contains(issues, x => x.Code == "DynamicTargetInvalid");
        Assert.DoesNotContain("untrusted-secret-name", JsonSerializer.Serialize(issues));
    }

    [Fact]
    public void missing_bound_leaf_does_not_disable_sibling_validation()
    {
        var descriptor = Descriptor("""{"type":"object","properties":{"parameters":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"value":{"type":"string","x-workerDock-dynamicValue":true}},"required":["name","value"]}}}}""");
        var config = JsonDocument.Parse("""{"parameters":[{}]}""").RootElement;
        var issues = TemplateSchemaValidator.ValidateAuthoring(descriptor, config, new HashSet<string>(["/parameters/0/value"]));
        Assert.Contains(issues, x => x.Path == "configuration.parameters[0].name" && x.Code == "Required");
        Assert.DoesNotContain(issues, x => x.Path == "configuration.parameters[0].value" && x.Code == "Required");
        var bindingIssues = DynamicValueValidation.Validate(descriptor, config, new(1,"UTC",[Bind("/parameters/0/value")]), "Scheduled");
        Assert.Contains(bindingIssues, x => x.Path == "configuration.parameters[0].name" && x.Code == "Required");
        Assert.DoesNotContain(bindingIssues, x => x.Path == "configuration.parameters[0].value" && x.Code == "Required");
    }

    [Fact]
    public void escaped_map_keys_and_local_refs_resolve()
    {
        var descriptor = Descriptor("""{"$defs":{"value":{"type":"string","x-workerDock-dynamicValue":true}},"type":"object","properties":{"values":{"type":"object","additionalProperties":{"$ref":"#/$defs/value"}}}}""");
        var config = JsonDocument.Parse("""{"values":{"a/b~c":"fixed"}}""").RootElement;
        var plan = new DynamicValuePlan(1, "UTC", [Bind("/values/a~1b~0c")]);
        Assert.Empty(DynamicValueValidation.Validate(descriptor, config, plan, "Scheduled"));
    }

    [Fact]
    public void only_selected_one_of_branch_targets_are_accepted()
    {
        var descriptor = Descriptor("""{"type":"object","oneOf":[{"type":"object","properties":{"kind":{"const":"a"},"a":{"type":"string","x-workerDock-dynamicValue":true}},"required":["kind","a"]},{"type":"object","properties":{"kind":{"const":"b"},"b":{"type":"string","x-workerDock-dynamicValue":true}},"required":["kind","b"]}]}""");
        var config = JsonDocument.Parse("""{"kind":"a","a":"fixed"}""").RootElement;
        Assert.Empty(DynamicValueValidation.Validate(descriptor, config, new(1,"UTC",[Bind("/a")]), "Scheduled"));
        Assert.Contains(DynamicValueValidation.Validate(descriptor, config, new(1,"UTC",[Bind("/b")]), "Scheduled"), x => x.Code == "DynamicTargetInvalid");
    }

    [Theory]
    [InlineData("/parameters/00/value")]
    [InlineData("/parameters/0~2/value")]
    [InlineData("/parameters/8/value")]
    [InlineData("/parameters/-/value")]
    public void malformed_or_nonexistent_indices_are_rejected_safely(string pointer)
    {
        var descriptor = Descriptor("""{"type":"object","properties":{"parameters":{"type":"array","items":{"type":"object","properties":{"value":{"type":"string","x-workerDock-dynamicValue":true}}}}}}""");
        var config = JsonDocument.Parse("""{"parameters":[{"value":"x"}]}""").RootElement;
        var issues = DynamicValueValidation.Validate(descriptor, config, new(1,"UTC",[Bind(pointer)]), "Scheduled");
        Assert.Contains(issues, x => x.Code is "DynamicTargetInvalid" or "DynamicBindingInvalid");
    }

    [Fact]
    public void rejects_type_mismatch_overlap_excess_and_sensitive_ancestor()
    {
        var descriptor = Descriptor("""{"type":"object","properties":{"safe":{"type":"object","properties":{"value":{"type":"integer","x-workerDock-dynamicValue":true}}},"secret":{"type":"object","x-workerDock-sensitive":true,"properties":{"value":{"type":"string","x-workerDock-dynamicValue":true}}}}}""");
        var config = JsonDocument.Parse("""{"safe":{"value":1},"secret":{"value":"x"}}""").RootElement;
        var mismatch = DynamicValueValidation.Validate(descriptor, config, new(1,"UTC",[Bind("/safe/value")]), "Scheduled");
        Assert.Contains(mismatch, x => x.Code == "DynamicTypeMismatch");
        var hidden = DynamicValueValidation.Validate(descriptor, config, new(1,"UTC",[Bind("/secret/value")]), "Scheduled");
        Assert.Contains(hidden, x => x.Code == "DynamicTargetInvalid");
        var overlap = DynamicValueValidation.Validate(descriptor, config, new(1,"UTC",[Bind("/safe"),Bind("/safe/value")]), "Scheduled");
        Assert.Contains(overlap, x => x.Code == "DynamicBindingInvalid");
        var many = Enumerable.Range(0,129).Select(i => Bind("/safe/value"+i)).ToArray();
        var tooMany = DynamicValueValidation.Validate(descriptor, config, new(1,"UTC",many), "Scheduled");
        Assert.Contains(tooMany, x => x.Code == "DynamicBindingInvalid");
    }

    [Fact]
    public void apply_replaces_only_existing_validated_scalar_leaves()
    {
        var config = JsonDocument.Parse("""{"a":[{"v":"old"}]}""").RootElement;
        var values = new Dictionary<string, JsonElement> { ["/a/0/v"] = JsonDocument.Parse("42").RootElement };
        var result = DynamicValueTargets.Apply(config, values);
        Assert.Equal(42, result.GetProperty("a")[0].GetProperty("v").GetInt32());
    }
}
