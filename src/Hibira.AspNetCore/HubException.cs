namespace Hibira.AspNetCore;

/// <summary>
/// Throw it from a hub method to send its message to the caller. Other
/// exceptions reach the caller as a generic error unless
/// <see cref="HibiraHubOptions.EnableDetailedErrors"/> is on.
/// </summary>
public class HubException : Exception
{
    /// <summary>Creates the exception.</summary>
    public HubException() { }

    /// <summary>Creates the exception with a message for the caller.</summary>
    public HubException(string? message) : base(message) { }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public HubException(string? message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>Gives a hub method another name for clients to invoke.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class HubMethodNameAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    public HubMethodNameAttribute(string name) => Name = name;

    /// <summary>The name clients use.</summary>
    public string Name { get; }
}

/// <summary>Sets the hub's name in its channel names (default: the class name without "Hub", lowercased).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class HibiraHubNameAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    public HibiraHubNameAttribute(string name) => Name = name;

    /// <summary>The name: letters, digits, "_" and "-", at most 40 characters.</summary>
    public string Name { get; }
}
