namespace WorkerDock.Contracts.Templates;

public sealed record FailureDiagnostic(
    string Kind,
    string? ExceptionType = null,
    string? Message = null,
    string? StackTrace = null,
    string? ResponseContent = null,
    int? SqlNumber = null,
    int? SqlState = null,
    string? SqlProcedure = null,
    int? SqlLine = null,
    string? CommandKind = null,
    string? HttpMethod = null,
    int? HttpStatus = null,
    string? MediaType = null,
    int? ResponseBytes = null,
    bool Truncated = false,
    bool Hidden = false);
