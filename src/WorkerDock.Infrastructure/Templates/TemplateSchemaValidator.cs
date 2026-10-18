using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorkerDock.Contracts.Templates;
namespace WorkerDock.Infrastructure.Templates;

/// <summary>Closed JSON Schema 2020-12 subset shared by server validation and built-in form controls.</summary>
public static class TemplateSchemaValidator
{
    public const int MaximumDocumentBytes = 49152;
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "$schema","$defs","$ref","type","properties","required","additionalProperties","items","minItems","maxItems","uniqueItems",
        "enum","const","minimum","maximum","exclusiveMinimum","exclusiveMaximum","multipleOf","minLength","maxLength","pattern","format","oneOf",
        "title","description","default","x-workerDock-secretReference","x-workerDock-sensitive","x-workerDock-labelKey","x-workerDock-descriptionKey"
    };
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal) { "object","array","string","integer","number","boolean","null" };
    private static readonly HashSet<string> Controls = new(StringComparer.Ordinal) { "text","textarea","number","checkbox","select","secretReference","sensitive" };
    public static IReadOnlyList<ValidationIssue> ValidateProviderOptions(WorkerDock.Contracts.Secrets.SecretProviderDescriptor descriptor,JsonElement options) =>
        ValidateNonsecretSettings(new(descriptor.Id,descriptor.Version,descriptor.DisplayName,descriptor.Description,descriptor.NonsecretOptionsSchema,descriptor.UiHints,""),options);
    public static IReadOnlyList<ValidationIssue> ValidateNonsecretSettings(TemplateDescriptor descriptor,JsonElement configuration)
    {
        var issues=Validate(descriptor,configuration).ToList();
        if(issues.Count==0) InspectNonsecret(descriptor.ConfigurationSchema,"schema",issues);
        return issues;
    }
    private static void InspectNonsecret(JsonElement schema,string path,List<ValidationIssue> issues)
    {
        if(schema.ValueKind!=JsonValueKind.Object || issues.Count>=64) return;
        if(schema.TryGetProperty("x-workerDock-sensitive",out var sensitive) && sensitive.ValueKind==JsonValueKind.True &&
            !(schema.TryGetProperty("x-workerDock-secretReference",out var reference) && reference.ValueKind==JsonValueKind.True)) issues.Add(new(path,"SecretReferenceRequired"));
        foreach(var p in schema.EnumerateObject())
        {
            if(p.Name is "properties" or "$defs") foreach(var child in p.Value.EnumerateObject()) InspectNonsecret(child.Value,path+"."+child.Name,issues);
            else if(p.Name=="oneOf") foreach(var branch in p.Value.EnumerateArray()) InspectNonsecret(branch,path+".oneOf",issues);
            else if(p.Name is "items" or "additionalProperties") InspectNonsecret(p.Value,path+"."+p.Name,issues);
        }
    }
    public static IReadOnlyList<ValidationIssue> ValidateDescriptor(TemplateDescriptor descriptor)
    {
        var issues=new List<ValidationIssue>();
        if (!Identifier(descriptor.Id,100) || !Identifier(descriptor.Version,40) || !Text(descriptor.DisplayName,200) || !Text(descriptor.Description,2000))
            issues.Add(new("metadata","InvalidMetadata"));
        if (descriptor.ConfigurationSchema.ValueKind!=JsonValueKind.Object || descriptor.UiHints.ValueKind!=JsonValueKind.Object)
        { issues.Add(new("metadata","InvalidSchema")); return issues; }
        if (System.Text.Encoding.UTF8.GetByteCount(descriptor.ConfigurationSchema.GetRawText()) + System.Text.Encoding.UTF8.GetByteCount(descriptor.UiHints.GetRawText()) > MaximumDocumentBytes)
        { issues.Add(new("metadata","MetadataTooLarge")); return issues; }
        Inspect(descriptor.ConfigurationSchema,descriptor.ConfigurationSchema,"schema",issues,0,new HashSet<string>(StringComparer.Ordinal));
        InspectHints(descriptor.UiHints,descriptor.ConfigurationSchema,issues);
        return issues;
    }
    public static IReadOnlyList<ValidationIssue> Validate(TemplateDescriptor descriptor,JsonElement configuration)
    {
        var issues=ValidateDescriptor(descriptor).ToList();
        if (issues.Count!=0) return issues;
        if (configuration.ValueKind==JsonValueKind.Undefined || System.Text.Encoding.UTF8.GetByteCount(configuration.GetRawText())>MaximumDocumentBytes)
            return [new("configuration","ConfigurationTooLarge")];
        Check(descriptor.ConfigurationSchema,descriptor.ConfigurationSchema,configuration,"configuration",issues,0);
        return issues;
    }
    private static void Inspect(JsonElement root,JsonElement schema,string path,List<ValidationIssue> issues,int depth,HashSet<string> references,int[]? budget=null)
    {
        budget ??= [4096];
        if (--budget[0]<0 || depth>16 || issues.Count>=64) { Add("InvalidSchema"); return; }
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False) return;
        if (schema.ValueKind!=JsonValueKind.Object) { Add("InvalidSchema"); return; }
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in schema.EnumerateObject())
        {
            if (!names.Add(p.Name)) Add("InvalidSchema");
            if (!Keywords.Contains(p.Name)) { Add("UnsupportedSchemaKeyword"); continue; }
            var value=p.Value;
            switch(p.Name)
            {
                case "$schema": if (value.ValueKind!=JsonValueKind.String || value.GetString()!="https://json-schema.org/draft/2020-12/schema") Add("InvalidSchema"); break;
                case "$ref":
                    if (value.ValueKind!=JsonValueKind.String) { Add("InvalidSchema"); break; }
                    var reference=value.GetString()!;
                    if (!reference.StartsWith('#')) { Add("RemoteSchemaReference"); break; }
                    if (!Resolve(root,reference,out var target) || !references.Add(reference)) { Add("InvalidSchema"); break; }
                    Inspect(root,target,path,issues,depth+1,references,budget); references.Remove(reference); break;
                case "$defs": case "properties":
                    if(value.ValueKind!=JsonValueKind.Object) { Add("InvalidSchema"); break; }
                    var keys=new HashSet<string>(StringComparer.Ordinal);
                    foreach(var child in value.EnumerateObject())
                    {
                        if (!Identifier(child.Name,100) || !keys.Add(child.Name)) Add("InvalidSchema");
                        Inspect(root,child.Value,path+"."+p.Name+"."+(Identifier(child.Name,100)?child.Name:"field"),issues,depth+1,references,budget);
                    }
                    break;
                case "type":
                    if(value.ValueKind==JsonValueKind.String) { if (!Types.Contains(value.GetString()!)) Add("InvalidSchema"); }
                    else if(value.ValueKind!=JsonValueKind.Array || value.GetArrayLength()==0 || value.EnumerateArray().Any(x=>x.ValueKind!=JsonValueKind.String || !Types.Contains(x.GetString()!)) || value.EnumerateArray().Select(x=>x.GetRawText()).Distinct().Count()!=value.GetArrayLength()) Add("InvalidSchema");
                    break;
                case "required":
                    if(value.ValueKind!=JsonValueKind.Array || value.EnumerateArray().Any(x=>x.ValueKind!=JsonValueKind.String || !Identifier(x.GetString()!,100)) || value.EnumerateArray().Select(x=>x.GetRawText()).Distinct().Count()!=value.GetArrayLength()) Add("InvalidSchema");
                    break;
                case "additionalProperties": case "items": Inspect(root,value,path+"."+p.Name,issues,depth+1,references,budget); break;
                case "oneOf":
                    if(value.ValueKind!=JsonValueKind.Array || value.GetArrayLength() is <2 or >16) { Add("InvalidSchema"); break; }
                    foreach(var alternative in value.EnumerateArray()) Inspect(root,alternative,path+".oneOf",issues,depth+1,references,budget);
                    if(!Discriminated(value)) Add("InvalidSchema");
                    break;
                case "minItems": case "maxItems": case "minLength": case "maxLength":
                    if(value.ValueKind!=JsonValueKind.Number || !value.TryGetInt32(out var bound) || bound is <0 or >16384) Add("InvalidSchema"); break;
                case "minimum": case "maximum": case "exclusiveMinimum": case "exclusiveMaximum": case "multipleOf":
                    if(!TryExactDecimal(value,out var n) || p.Name=="multipleOf" && n<=0) Add("InvalidSchema"); break;
                case "pattern": if(value.ValueKind!=JsonValueKind.String || !PortablePattern(value.GetString()!)) Add("InvalidSchema"); break;
                case "format": if(value.ValueKind!=JsonValueKind.String || value.GetString() is not ("uuid" or "date-time")) Add("UnsupportedSchemaFormat"); break;
                case "uniqueItems": case "x-workerDock-secretReference": case "x-workerDock-sensitive":
                    if(value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Add("InvalidSchema"); break;
                case "title": case "description": if(value.ValueKind!=JsonValueKind.String || !Text(value.GetString()!,2000)) Add("InvalidSchema"); break;
                case "x-workerDock-labelKey": case "x-workerDock-descriptionKey": if(value.ValueKind!=JsonValueKind.String || !Identifier(value.GetString()!,160)) Add("InvalidSchema"); break;
                case "enum":
                    if(value.ValueKind!=JsonValueKind.Array || value.GetArrayLength() is <1 or >128) { Add("InvalidSchema"); break; }
                    var members=value.EnumerateArray().ToArray();
                    for(var i=0;i<members.Length;i++) for(var j=0;j<i;j++) if(JsonElement.DeepEquals(members[i],members[j])) Add("InvalidSchema");
                    break;
            }
        }
        foreach(var pair in new[] { ("minLength","maxLength"),("minItems","maxItems"),("minimum","maximum"),("exclusiveMinimum","exclusiveMaximum") })
            if(schema.TryGetProperty(pair.Item1,out var lower) && schema.TryGetProperty(pair.Item2,out var upper) && TryExactDecimal(lower,out var lo) && TryExactDecimal(upper,out var hi) && lo>hi) Add("InvalidSchema");
        if(schema.TryGetProperty("x-workerDock-secretReference",out var secretReference) && secretReference.ValueKind==JsonValueKind.True &&
            (!schema.TryGetProperty("type",out var referenceType) || referenceType.ValueKind!=JsonValueKind.String || referenceType.GetString()!="string" || schema.TryGetProperty("default",out _))) Add("InvalidSchema");
        void Add(string code) { if(issues.Count<64) issues.Add(new(path,code)); }
    }
    private static bool Discriminated(JsonElement alternatives)
    {
        var first=alternatives[0];
        if(first.ValueKind!=JsonValueKind.Object || !first.TryGetProperty("properties",out var props) || props.ValueKind!=JsonValueKind.Object) return false;
        foreach(var property in props.EnumerateObject())
        {
            var values=new List<JsonElement>();
            foreach(var branch in alternatives.EnumerateArray())
            {
                if(branch.ValueKind!=JsonValueKind.Object || !branch.TryGetProperty("required",out var required) || required.ValueKind!=JsonValueKind.Array || !required.EnumerateArray().Any(x=>x.ValueKind==JsonValueKind.String && x.GetString()==property.Name) ||
                   !branch.TryGetProperty("properties",out var properties) || properties.ValueKind!=JsonValueKind.Object || !properties.TryGetProperty(property.Name,out var definition) || definition.ValueKind!=JsonValueKind.Object || !definition.TryGetProperty("const",out var constant) || constant.ValueKind!=JsonValueKind.String) break;
                values.Add(constant);
            }
            if(values.Count==alternatives.GetArrayLength() && values.Select(x=>x.GetString()).Distinct(StringComparer.Ordinal).Count()==values.Count) return true;
        }
        return false;
    }
    private static void InspectHints(JsonElement hints,JsonElement schema,List<ValidationIssue> issues)
    {
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in hints.EnumerateObject())
        {
            if(!seen.Add(p.Name)) Invalid();
            if(p.Name is "labelKey" or "descriptionKey") { if(p.Value.ValueKind!=JsonValueKind.String || !Identifier(p.Value.GetString()!,160)) Invalid(); continue; }
            if(p.Name!="fields" || p.Value.ValueKind!=JsonValueKind.Object) { Invalid(); continue; }
            var fields=new HashSet<string>(StringComparer.Ordinal);
            foreach(var field in p.Value.EnumerateObject())
            {
                if(!Identifier(field.Name,100) || !fields.Add(field.Name) || !schema.TryGetProperty("properties",out var properties) || properties.ValueKind!=JsonValueKind.Object || !properties.TryGetProperty(field.Name,out _) || field.Value.ValueKind!=JsonValueKind.Object) { Invalid(); continue; }
                var keys=new HashSet<string>(StringComparer.Ordinal);
                foreach(var hint in field.Value.EnumerateObject())
                {
                    if(!keys.Add(hint.Name) || hint.Value.ValueKind!=JsonValueKind.String) { Invalid(); continue; }
                    var value=hint.Value.GetString()!;
                    if(hint.Name=="control") { if(!Controls.Contains(value)) Invalid(); }
                    else if(hint.Name is "labelKey" or "descriptionKey" or "placeholderKey") { if(!Identifier(value,160)) Invalid(); }
                    else Invalid();
                }
            }
        }
        void Invalid() { if(issues.Count<64) issues.Add(new("uiHints","InvalidUiHints")); }
    }
    private static void Check(JsonElement root,JsonElement schema,JsonElement value,string path,List<ValidationIssue> issues,int depth,int[]? budget=null)
    {
        budget ??= [32768];
        if(--budget[0]<0) { if(issues.Count<64) issues.Add(new(path,"ValidationBudgetExceeded")); return; }
        if(issues.Count>=64) return;
        if(depth>32) { Add("ConfigurationTooDeep"); return; }
        if(schema.ValueKind==JsonValueKind.True) return;
        if(schema.ValueKind==JsonValueKind.False) { Add("SchemaRejected"); return; }
        if(schema.TryGetProperty("$ref",out var reference)) { Resolve(root,reference.GetString()!,out var target); Check(root,target,value,path,issues,depth+1,budget); }
        if(schema.TryGetProperty("type",out var type) && !(type.ValueKind==JsonValueKind.Array ? type.EnumerateArray().Any(x=>Matches(x.GetString()!,value)) : Matches(type.GetString()!,value))) { Add("Type"); return; }
        if(schema.TryGetProperty("x-workerDock-secretReference",out var secret) && secret.ValueKind==JsonValueKind.True && (value.ValueKind!=JsonValueKind.String || !Identifier(value.GetString()!,128))) Add("SecretReferenceInvalid");
        if(schema.TryGetProperty("enum",out var allowed) && !allowed.EnumerateArray().Any(x=>JsonElement.DeepEquals(x,value))) Add("Enum");
        if(schema.TryGetProperty("const",out var constant) && !JsonElement.DeepEquals(constant,value)) Add("Const");
        if(schema.TryGetProperty("oneOf",out var alternatives))
        {
            var matches=0;
            foreach(var branch in alternatives.EnumerateArray()) { var branchIssues=new List<ValidationIssue>(); Check(root,branch,value,path,branchIssues,depth+1,budget); if(branchIssues.Count==0) matches++; }
            if(budget[0]<0) { Add("ValidationBudgetExceeded"); return; }
            if(matches!=1) Add("OneOf");
        }
        switch(value.ValueKind)
        {
            case JsonValueKind.Object:
                var propertyNames=new HashSet<string>(StringComparer.Ordinal);
                if(schema.TryGetProperty("required",out var required)) foreach(var name in required.EnumerateArray()) if(!value.TryGetProperty(name.GetString()!,out _)) Add("Required");
                foreach(var property in value.EnumerateObject())
                {
                    if(!propertyNames.Add(property.Name)) Add("DuplicateProperty");
                    if(schema.TryGetProperty("properties",out var properties) && properties.TryGetProperty(property.Name,out var child)) Check(root,child,property.Value,path+"."+property.Name,issues,depth+1,budget);
                    else if(schema.TryGetProperty("additionalProperties",out var additional))
                    { if(additional.ValueKind==JsonValueKind.False) Add("AdditionalProperty"); else Check(root,additional,property.Value,path+".additional",issues,depth+1,budget); }
                }
                break;
            case JsonValueKind.Array:
                if(Bound("minItems",value.GetArrayLength(),"MinItems",false) || Bound("maxItems",value.GetArrayLength(),"MaxItems",true)) break;
                var array=value.EnumerateArray().ToArray();
                if(schema.TryGetProperty("uniqueItems",out var unique) && unique.ValueKind==JsonValueKind.True)
                {
                    for(var i=0;i<array.Length;i++) for(var j=0;j<i;j++)
                    {
                        if(--budget[0]<0) { Add("ValidationBudgetExceeded"); return; }
                        if(JsonElement.DeepEquals(array[i],array[j])) { Add("UniqueItems"); return; }
                    }
                }
                if(schema.TryGetProperty("items",out var item)) foreach(var element in array) Check(root,item,element,path+"[]",issues,depth+1,budget);
                break;
            case JsonValueKind.String:
                var text=value.GetString()!; var length=text.EnumerateRunes().Count();
                if(length>16384) { Add("MaxLength"); break; }
                Bound("minLength",length,"MinLength",false); Bound("maxLength",length,"MaxLength",true);
                if(schema.TryGetProperty("pattern",out var pattern) && !Regex.IsMatch(text,pattern.GetString()!,RegexOptions.CultureInvariant|RegexOptions.NonBacktracking,TimeSpan.FromMilliseconds(50))) Add("Pattern");
                if(schema.TryGetProperty("format",out var format) && !(format.GetString()=="uuid" ? Guid.TryParseExact(text,"D",out _) : DateTimeOffset.TryParseExact(text,["yyyy-MM-dd'T'HH:mm:ssK","yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],CultureInfo.InvariantCulture,DateTimeStyles.None,out _) && (text.EndsWith('Z') || Regex.IsMatch(text,@"[+-][0-9]{2}:[0-9]{2}$")))) Add("Format");
                break;
            case JsonValueKind.Number:
                if(!TryExactDecimal(value,out var number)) { Add("Number"); break; }
                Bound("minimum",number,"Minimum",false); Bound("maximum",number,"Maximum",true);
                if(schema.TryGetProperty("exclusiveMinimum",out var min) && number<=min.GetDecimal()) Add("ExclusiveMinimum");
                if(schema.TryGetProperty("exclusiveMaximum",out var max) && number>=max.GetDecimal()) Add("ExclusiveMaximum");
                if(schema.TryGetProperty("multipleOf",out var multiple))
                {
                    if(TryExactDecimal(multiple,out var divisor) && divisor>0) { if(number%divisor!=0) Add("MultipleOf"); }
                    else Add("Number");
                }
                break;
        }
        void Add(string code) { if(issues.Count<64) issues.Add(new(path,code)); }
        bool Bound(string key,decimal actual,string code,bool upper) { if(!schema.TryGetProperty(key,out var n) || !(upper?actual>n.GetDecimal():actual<n.GetDecimal())) return false; Add(code); return true; }
    }
    // TryGetDecimal can round excess digits; inspect normalized significant digits/scale first.
    private static bool TryExactDecimal(JsonElement number,out decimal exact)
    {
        exact=0;
        if(number.ValueKind!=JsonValueKind.Number) return false;
        var raw=number.GetRawText(); var exponentAt=raw.AsSpan().IndexOfAny('e','E'); var exponent=0;
        if(exponentAt>=0 && !int.TryParse(raw.AsSpan(exponentAt+1),NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out exponent)) return false;
        var mantissa=(exponentAt<0?raw:raw[..exponentAt]).TrimStart('-'); var point=mantissa.IndexOf('.');
        var digits=mantissa.Replace(".","").TrimStart('0'); var significant=digits.TrimEnd('0');
        if(significant.Length==0) return true;
        var scale=(long)(point<0?0:mantissa.Length-point-1)-exponent-(digits.Length-significant.Length);
        if(significant.Length>28 || scale is >28 or <-28) return false;
        return number.TryGetDecimal(out exact);
    }
    private static bool Matches(string type,JsonElement value) => type switch
    {
        "object"=>value.ValueKind==JsonValueKind.Object,"array"=>value.ValueKind==JsonValueKind.Array,"string"=>value.ValueKind==JsonValueKind.String,
        "boolean"=>value.ValueKind is JsonValueKind.True or JsonValueKind.False,"null"=>value.ValueKind==JsonValueKind.Null,
        "number"=>value.ValueKind==JsonValueKind.Number,"integer"=>value.ValueKind==JsonValueKind.Number && TryExactDecimal(value,out var n) && decimal.Truncate(n)==n,_=>false
    };
    private static bool Resolve(JsonElement root,string reference,out JsonElement target)
    {
        target=default;
        return reference.StartsWith("#/$defs/",StringComparison.Ordinal) && Identifier(reference[8..],100) && root.TryGetProperty("$defs",out var defs) && defs.ValueKind==JsonValueKind.Object && defs.TryGetProperty(reference[8..],out target);
    }
    private static bool PortablePattern(string pattern)
    {
        if(pattern.Length is <1 or >256 || pattern.Any(c=>!char.IsAscii(c) || char.IsControl(c) || c is '(' or ')' or '|' or '\\')) return false;
        var characterClass=false;
        foreach(var c in pattern)
        {
            if(c=='[') { if(characterClass) return false; characterClass=true; }
            if(c==']') { if(!characterClass) return false; characterClass=false; }
        }
        if(characterClass) return false;
        try { _=new Regex(pattern,RegexOptions.CultureInvariant|RegexOptions.NonBacktracking,TimeSpan.FromMilliseconds(50)); return true; }
        catch(ArgumentException) { return false; }
        catch(NotSupportedException) { return false; }
    }
    private static bool Identifier(string value,int maximum) => value is { Length: >0 } && value.Length<=maximum && value.All(c=>char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    private static bool Text(string value,int maximum) => value is not null && value.Length<=maximum && !value.Any(c=>c is '<' or '>' or '&' || char.IsControl(c) && c is not ('\r' or '\n' or '\t'));
}
