using System.Net;

namespace Hibira;

/// <summary>The Hibira HTTP API refused a request.</summary>
public sealed class HibiraException : Exception
{
    /// <summary>Creates the exception.</summary>
    public HibiraException(HttpStatusCode statusCode, string message, string? body = null)
        : base($"Hibira HTTP API answered {(int)statusCode}: {message}")
    {
        StatusCode = statusCode;
        Body = body;
    }

    /// <summary>The HTTP status code.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>The response body, if any.</summary>
    public string? Body { get; }
}
