using System.Text.Json;
using WorkerDock.Application.Common;
using WorkerDock.Contracts.Templates;
using WorkerDock.Infrastructure.Templates;
namespace WorkerDock.IntegrationTests;
public sealed partial class TemplateCatalogTests
{
    internal static TemplateDescriptor Descriptor(string schema = """{"type":"object","properties":{"name":{"type":"string","maxLength":40}},"required":["name"],"additionalProperties":false}""", string hints="{}") => TemplateMetadata.WithDigest(new("test.template","1.0.0","Test","Test template",JsonSerializer.Deserialize<JsonElement>(schema),JsonSerializer.Deserialize<JsonElement>(hints),""));
    [Fact]
    public async Task Digest_cannot_change_under_same_id_and_version()
    {
        var original=Descriptor(); var changed=TemplateMetadata.WithDigest(original with { Description="changed" });
        var catalog=new TemplateCatalog([original,changed]);
        var digest=await catalog.RegisterAsync([original],default);
        Assert.Equal(64,digest.Length);
        var error=await Assert.ThrowsAsync<WorkerDockException>(async () => await catalog.RegisterAsync([changed],default));
        Assert.Equal("MetadataVersionConflict",error.Code);
        Assert.Equal(original,await catalog.GetAsync(original.Id,original.Version,default));
        await catalog.RetireAsync(original.Id,original.Version,default);
        Assert.False(await catalog.IsSelectableAsync(original.Id,original.Version,default));
        await catalog.RegisterAsync([original],default);
        Assert.False(await catalog.IsSelectableAsync(original.Id,original.Version,default));
        Assert.Single(await catalog.ListAsync(default));
    }
    [Fact]
    public async Task Registration_is_atomic_trusted_and_copies_document_lifetime()
    {
        var original=Descriptor(); var other=TemplateMetadata.WithDigest(original with { Id="other" });
        var catalog=new TemplateCatalog([original]);
        var error=await Assert.ThrowsAsync<WorkerDockException>(async () => await catalog.RegisterAsync([original,other],default));
        Assert.Equal("UntrustedTemplate",error.Code);
        Assert.Empty(await catalog.ListAsync(default));
        using(var doc=JsonDocument.Parse(original.ConfigurationSchema.GetRawText()))
            await catalog.RegisterAsync([original with { ConfigurationSchema=doc.RootElement }],default);
        Assert.Equal("object",(await catalog.GetAsync(original.Id,original.Version,default))!.ConfigurationSchema.GetProperty("type").GetString());
    }
    [Theory]
    [InlineData("""{"type":"object","$ref":"https://host/schema"}""","{}","RemoteSchemaReference")]
    [InlineData("""{"type":"object","unevaluatedProperties":false}""","{}","UnsupportedSchemaKeyword")]
    [InlineData("""{"type":"object","x-workerDock-sensitive":"true"}""","{}","InvalidSchema")]
    [InlineData("""{"type":"object"}""","""{"html":"<script>alert(1)</script>"}""","InvalidUiHints")]
    [InlineData("""{"type":"object"}""","""{"fields":{"name":{"control":"javascript"}}}""","InvalidUiHints")]
    public void Hostile_metadata_fails_closed(string schema,string hints,string code)
        => Assert.Contains(TemplateSchemaValidator.ValidateDescriptor(Descriptor(schema,hints)),x=>x.Code==code);
    [Fact]
    public void Supported_subset_validates_configuration_and_never_echoes_values()
    {
        var descriptor=Descriptor("""{"type":"object","$defs":{"ref":{"type":"string","minLength":1,"x-workerDock-secretReference":true}},"properties":{"credential":{"$ref":"#/$defs/ref"},"count":{"type":"integer","minimum":1,"maximum":5},"items":{"type":"array","maxItems":2,"items":{"type":"string","pattern":"^[a-z]+$"}}},"required":["credential"],"additionalProperties":false}""");
        Assert.Empty(TemplateSchemaValidator.ValidateDescriptor(descriptor));
        Assert.Empty(TemplateSchemaValidator.Validate(descriptor,JsonSerializer.Deserialize<JsonElement>("""{"credential":"logical","count":2,"items":["a"]}""")));
        var issues=TemplateSchemaValidator.Validate(descriptor,JsonSerializer.Deserialize<JsonElement>("""{"credential":"logical","count":9,"items":["BAD"],"extra":"SENTINEL"}"""));
        Assert.Contains(issues,x=>x.Code=="AdditionalProperty"); Assert.Contains(issues,x=>x.Code=="Maximum"); Assert.Contains(issues,x=>x.Code=="Pattern");
        Assert.DoesNotContain("SENTINEL",JsonSerializer.Serialize(issues));
    }
    [Fact]
    public void Only_exact_seeded_bootstrap_id_version_and_digest_can_be_promoted_once()
    {
        var seeded=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.Unicode.GetBytes("workerdock.http|1.0.0")));
        Assert.True(TemplateMetadata.IsBootstrapPlaceholder("workerdock.http","1.0.0",seeded,true));
        Assert.False(TemplateMetadata.IsBootstrapPlaceholder("workerdock.http","1.0.0",seeded,false));
        Assert.False(TemplateMetadata.IsBootstrapPlaceholder("workerdock.http","2.0.0",seeded,true));
        Assert.False(TemplateMetadata.IsBootstrapPlaceholder("custom","1.0.0",seeded,true));
        Assert.False(TemplateMetadata.IsBootstrapPlaceholder("workerdock.http","1.0.0",new string('A',64),true));
    }
    [Fact]
    public void Nonsecret_provider_configuration_requires_references_for_sensitive_fields()
    {
        var descriptor=Descriptor("""{"type":"object","properties":{"credential":{"type":"string","x-workerDock-sensitive":true}},"additionalProperties":false}""");
        Assert.Contains(TemplateSchemaValidator.ValidateNonsecretSettings(descriptor,JsonSerializer.SerializeToElement(new { credential="SYNTHETIC_PASSWORD" })),x=>x.Code=="SecretReferenceRequired");
        var references=Descriptor("""{"type":"object","properties":{"credential":{"type":"string","x-workerDock-secretReference":true}},"additionalProperties":false}""");
        Assert.Empty(TemplateSchemaValidator.ValidateNonsecretSettings(references,JsonSerializer.SerializeToElement(new { credential="logical.name" })));
        Assert.NotEmpty(TemplateSchemaValidator.Validate(references,JsonSerializer.SerializeToElement(new { credential="Server=db;Password=literal" })));
    }
    [Theory]
    [InlineData("""{"type":"object","properties":true}""")]
    [InlineData("""{"type":"object","required":[1]}""")]
    [InlineData("""{"type":"object","$defs":{"loop":{"$ref":"#/$defs/loop"}},"$ref":"#/$defs/loop"}""")]
    [InlineData("""{"type":"string","pattern":"(a+)+$"}""")]
    [InlineData("""{"type":"string","enum":["a","a"]}""")]
    [InlineData("""{"type":"object","minLength":5,"maxLength":2}""")]
    [InlineData("""{"type":"object","properties":{"x":{"type":"string","x-workerDock-secretReference":true,"default":"literal"}}}""")]
    [InlineData("""{"type":"string","pattern":"^[a-z-[x]]+$"}""")]
    [InlineData("""{"type":"string","pattern":"^a{100000}$"}""")]
    public void Malformed_or_unbounded_schema_is_rejected_without_exception(string schema)
        => Assert.NotEmpty(TemplateSchemaValidator.ValidateDescriptor(Descriptor(schema)));
    [Fact]
    public void Shared_schema_fixtures_define_server_and_client_compatibility()
    {
        using var fixtures=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","template-schemas.json")));
        foreach(var fixture in fixtures.RootElement.EnumerateArray())
        {
            var descriptor=Descriptor(fixture.GetProperty("schema").GetRawText());
            Assert.Empty(TemplateSchemaValidator.ValidateDescriptor(descriptor));
            var issues=TemplateSchemaValidator.Validate(descriptor,fixture.GetProperty("configuration"));
            Assert.True(fixture.GetProperty("valid").GetBoolean()==(issues.Count==0),fixture.GetProperty("name").GetString());
            if(fixture.TryGetProperty("code",out var code)) Assert.Contains(issues,x=>x.Code==code.GetString() && x.Path==fixture.GetProperty("path").GetString());
        }
    }
    [Theory]
    [InlineData("maximum","9007199254740992","9007199254740993",false)]
    [InlineData("minimum","9007199254740993","9007199254740992",false)]
    [InlineData("maximum","9007199254740992","9007199254740992",true)]
    [InlineData("minimum","9007199254740993","9007199254740993",true)]
    [InlineData("exclusiveMinimum","9007199254740992","9007199254740993",true)]
    [InlineData("exclusiveMaximum","9007199254740993","9007199254740992",true)]
    [InlineData("exclusiveMinimum","9007199254740992","9007199254740992",false)]
    [InlineData("exclusiveMaximum","9007199254740993","9007199254740993",false)]
    [InlineData("maximum","0.1234567890123456789012345678","0.1234567890123456789012345679",false)]
    [InlineData("maximum","9.007199254740992e15","9007199254740993",false)]
    public void Numeric_boundaries_do_not_round_distinct_JSON_numbers(string keyword,string bound,string number,bool valid)
    {
        var descriptor=Descriptor("{\"type\":\"number\",\""+keyword+"\":"+bound+"}");
        Assert.Empty(TemplateSchemaValidator.ValidateDescriptor(descriptor));
        Assert.Equal(valid,TemplateSchemaValidator.Validate(descriptor,JsonSerializer.Deserialize<JsonElement>(number)).Count==0);
    }
    [Fact]
    public void Unsupported_numeric_precision_is_rejected_in_schema_and_configuration()
    {
        Assert.NotEmpty(TemplateSchemaValidator.ValidateDescriptor(Descriptor("""{"type":"number","minimum":1e-29}""")));
        Assert.NotEmpty(TemplateSchemaValidator.Validate(Descriptor("""{"type":"number"}"""),JsonSerializer.Deserialize<JsonElement>("1.000000000000000000000000000001")));
    }
    [Fact]
    public void Uniqueness_comparisons_share_the_evaluation_budget()
    {
        var descriptor=Descriptor("""{"type":"array","uniqueItems":true}""");
        var issues=TemplateSchemaValidator.Validate(descriptor,JsonSerializer.SerializeToElement(Enumerable.Range(0,9000)));
        Assert.Contains(issues,x=>x.Code=="ValidationBudgetExceeded");
        var nested=Descriptor("""{"type":"array","items":{"type":"array","uniqueItems":true}}""");
        var nestedIssues=TemplateSchemaValidator.Validate(nested,JsonSerializer.SerializeToElement(new[] { Enumerable.Range(0,200).ToArray(),Enumerable.Range(0,200).ToArray() }));
        Assert.Contains(nestedIssues,x=>x.Code=="ValidationBudgetExceeded");
        var alternatives=Descriptor("""{"oneOf":[{"properties":{"kind":{"const":"a"},"values":{"type":"array"}},"required":["kind"]},{"properties":{"kind":{"const":"b"},"values":{"type":"array","uniqueItems":true}},"required":["kind"]}]}""");
        var branchIssues=TemplateSchemaValidator.Validate(alternatives,JsonSerializer.SerializeToElement(new { kind="a",values=Enumerable.Range(0,9000).ToArray() }));
        Assert.Contains(branchIssues,x=>x.Code=="ValidationBudgetExceeded");
    }
    [Fact]
    public void Decisive_array_length_failure_skips_uniqueness_and_item_evaluation()
    {
        var descriptor=Descriptor("""{"type":"array","maxItems":1,"uniqueItems":true,"items":{"type":"string"}}""");
        var issues=TemplateSchemaValidator.Validate(descriptor,JsonSerializer.SerializeToElement(Enumerable.Repeat(0,9000)));
        Assert.Equal("MaxItems",Assert.Single(issues).Code);
    }
}
