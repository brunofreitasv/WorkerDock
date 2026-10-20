using System.Text.RegularExpressions;
using WorkerDock.Contracts.Templates;
using WorkerDock.Contracts.Values;

namespace WorkerDock.Templates.Http;

public sealed partial class HttpConfigurationValidator(string version) : ITemplateAuthoringValidator
{
    public string TemplateId => "workerdock.http";
    public string TemplateVersion => version;

    public IReadOnlyList<ValidationIssue> ValidateAuthoring(TemplateAuthoringContext context)
    {
        if (version != "1.1.0" || context.Configuration.ValueKind != System.Text.Json.JsonValueKind.Object) return [];
        var known = new HashSet<string>(StringComparer.Ordinal);
        if (context.Configuration.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var item in parameters.EnumerateObject()) known.Add(item.Name);
        if (context.DynamicValues is { } values)
            foreach (var binding in values.Bindings)
                if (TryParameterName(binding.Target, out var name)) known.Add(name);

        var issues = new List<ValidationIssue>();
        foreach (var (field, path) in new[] { ("url", "configuration.url"), ("query", "configuration.query"), ("headers", "configuration.headers"), ("contentType", "configuration.contentType"), ("body", "configuration.body") })
        {
            if (!context.Configuration.TryGetProperty(field, out var value)) continue;
            if (value.ValueKind == System.Text.Json.JsonValueKind.String) Check(value.GetString()!, path);
            else if (value.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var component in value.EnumerateObject())
                    if (component.Value.ValueKind == System.Text.Json.JsonValueKind.String) Check(component.Value.GetString()!, path + "." + component.Name);
        }
        return issues.Take(64).ToArray();

        void Check(string text, string path)
        {
            foreach (Match match in Placeholder().Matches(text))
                if (match.Groups[1].Value != "execution.idempotencyKey" && !known.Contains(match.Groups[1].Value))
                    issues.Add(new(path, "HttpParameterMissing"));
        }
    }

    private static bool TryParameterName(string target, out string name)
    {
        name = "";
        if (!target.StartsWith("/parameters/", StringComparison.Ordinal)) return false;
        var segment = target[12..];
        if (segment.Length == 0 || segment.Contains('/')) return false;
        name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        return true;
    }

    [GeneratedRegex("\\{([A-Za-z][A-Za-z0-9_.-]{0,63})\\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
