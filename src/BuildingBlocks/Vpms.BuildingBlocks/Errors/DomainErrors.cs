namespace Vpms.BuildingBlocks.Errors;

/// <summary>The resource does not exist, or exists but is not visible to the caller (never reveal which).</summary>
public sealed class NotFoundException(string resource) : Exception($"{resource} was not found.")
{
    public string Resource { get; } = resource;
}

/// <summary>The request is valid but conflicts with the current state (invalid transition, duplicate, stale data).</summary>
public sealed class ConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The request content is not acceptable. <see cref="Code"/> is a stable machine-readable reason.</summary>
public sealed class ValidationFailedException(string code, string message, int statusCode = 400) : Exception(message)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;
}
