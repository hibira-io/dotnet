using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hibira.AspNetCore;

/// <summary>Everything one hub type needs at run time.</summary>
internal sealed class HubRuntime
{
    private readonly Type _hubType;
    private readonly Dictionary<string, HubMethod> _methods;
    private readonly ILogger _logger;

    public HubRuntime(Type hubType, string name, IHibiraClient client, HibiraHubOptions options, IHibiraConnectionStore store, ILoggerFactory loggers)
    {
        _hubType = hubType;
        Channels = new HubChannels(name);
        Sender = new HubSender(Channels, client, options);
        Store = store;
        Options = options;
        _logger = loggers.CreateLogger("Hibira.AspNetCore.HibiraHub");
        Groups = new GroupManager(Sender, store, _logger);
        _methods = HubMethod.Discover(hubType);
        TypedClient = hubType.BaseType is { IsGenericType: true } b && b.GetGenericTypeDefinition() == typeof(HibiraHub<>)
            ? b.GetGenericArguments()[0]
            : FindTypedClient(hubType);
    }

    public HubChannels Channels { get; }
    public HubSender Sender { get; }
    public IHibiraConnectionStore Store { get; }
    public HibiraHubOptions Options { get; }
    public IGroupManager Groups { get; }
    public Type? TypedClient { get; }
    public IHibiraClient Client => Sender.Client;
    public ILogger Logger => _logger;

    private static Type? FindTypedClient(Type t)
    {
        for (var c = t; c != null; c = c.BaseType)
            if (c.IsGenericType && c.GetGenericTypeDefinition() == typeof(HibiraHub<>)) return c.GetGenericArguments()[0];
        return null;
    }

    public string Token(string connectionId, string? userId) =>
        HibiraSignature.Hmac(Client.Options.Secret, "hibira-hub\n" + Channels.Hub + "\n" + connectionId + "\n" + (userId ?? ""));

    public bool CheckToken(string connectionId, string? userId, string? token) =>
        HubChannels.IsSocketId(connectionId) && HibiraSignature.Equal(Token(connectionId, userId), token);

    public HibiraHub CreateHub(IServiceProvider services, string connectionId, ClaimsPrincipal user, HttpContext? http)
    {
        var hub = (HibiraHub)(services.GetService(_hubType) ?? ActivatorUtilities.CreateInstance(services, _hubType));
        hub.Context = new HubCallerContext(connectionId, user, Options.UserIdProvider(user), http);
        var clients = new HubCallerClients(Sender, connectionId);
        hub.Clients = clients;
        hub.Groups = Groups;
        if (TypedClient != null)
        {
            var make = typeof(HubRuntime).GetMethod(nameof(MakeTypedClients), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(TypedClient);
            var typed = make.Invoke(null, new object[] { Sender, connectionId })!;
            FindTypedClientsProperty(_hubType)?.SetValue(hub, typed);
        }
        return hub;
    }

    private static PropertyInfo? FindTypedClientsProperty(Type t)
    {
        for (var c = t; c != null; c = c.BaseType)
            if (c.IsGenericType && c.GetGenericTypeDefinition() == typeof(HibiraHub<>))
                return c.GetProperty("Clients", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        return null;
    }

    private static IHubCallerClients<T> MakeTypedClients<T>(HubSender sender, string caller) where T : class =>
        new HubCallerClientsBase<T>(sender, TypedClientProxy.Create<T>, caller);

    internal static IHubClients<T> MakeTypedHubClients<T>(HubSender sender) where T : class =>
        new HubClientsBase<T>(sender, TypedClientProxy.Create<T>);

    public async Task<InvocationResult> InvokeAsync(HttpContext http, string connectionId, string target, JsonElement arguments)
    {
        if (!_methods.TryGetValue(target, out var method))
            return InvocationResult.Fail($"Method does not exist: {target}");

        if (method.Authorize.Count > 0)
        {
            var policyProvider = http.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>();
            var policy = await AuthorizationPolicy.CombineAsync(policyProvider, method.Authorize).ConfigureAwait(false);
            if (policy != null)
            {
                var auth = await http.RequestServices.GetRequiredService<IAuthorizationService>().AuthorizeAsync(http.User, policy).ConfigureAwait(false);
                if (!auth.Succeeded) return InvocationResult.Fail($"Failed to invoke '{target}' because user is unauthorized");
            }
        }

        object?[] args;
        try
        {
            args = method.BindArguments(arguments, http.RequestServices, Options.JsonSerializerOptions, http.RequestAborted);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or NotSupportedException)
        {
            return InvocationResult.Fail($"Failed to invoke '{target}' due to an error on the server. {(Options.EnableDetailedErrors ? e.Message : "InvalidDataException: Invalid arguments.")}");
        }

        var hub = CreateHub(http.RequestServices, connectionId, http.User, http);
        try
        {
            var value = await method.InvokeAsync(hub, args).ConfigureAwait(false);
            return InvocationResult.Ok(method.ReturnsValue ? value : null, method.ReturnsValue);
        }
        catch (Exception e)
        {
            return InvocationResult.Fail(ErrorMessage($"invoking '{target}'", e));
        }
        finally
        {
            await DisposeAsync(hub).ConfigureAwait(false);
        }
    }

    public string ErrorMessage(string what, Exception e)
    {
        if (e is TargetInvocationException { InnerException: { } inner }) e = inner;
        if (e is HubException) return $"An unexpected error occurred {what} on the server. HubException: {e.Message}";
        _logger.LogError(e, "Hibira: error {What} on hub {Hub}.", what, Channels.Hub);
        return Options.EnableDetailedErrors
            ? $"An unexpected error occurred {what} on the server. {e.GetType().Name}: {e.Message}"
            : $"An unexpected error occurred {what} on the server.";
    }

    public static async ValueTask DisposeAsync(HibiraHub hub)
    {
        if (hub is IAsyncDisposable a) await a.DisposeAsync().ConfigureAwait(false);
        else hub.Dispose();
    }
}

internal readonly record struct InvocationResult(bool Success, object? Value, bool HasValue, string? Error)
{
    public static InvocationResult Ok(object? value, bool hasValue) => new(true, value, hasValue, null);
    public static InvocationResult Fail(string error) => new(false, null, false, error);
}

internal sealed class HubMethod
{
    private readonly MethodInfo _method;
    private readonly ParameterInfo[] _parameters;
    private readonly Func<object?, Task<object?>> _await;

    private HubMethod(MethodInfo method)
    {
        _method = method;
        _parameters = method.GetParameters();
        Authorize = method.GetCustomAttributes<AuthorizeAttribute>(true).Cast<IAuthorizeData>().ToList();
        (_await, ReturnsValue) = MakeAwaiter(method.ReturnType);
    }

    public IReadOnlyList<IAuthorizeData> Authorize { get; }
    public bool ReturnsValue { get; }

    public static Dictionary<string, HubMethod> Discover(Type hubType)
    {
        var excluded = new HashSet<string>(typeof(HibiraHub).GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(m => m.Name), StringComparer.Ordinal);
        var result = new Dictionary<string, HubMethod>(StringComparer.OrdinalIgnoreCase);
        for (var t = hubType; t != null && t != typeof(HibiraHub) && !(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(HibiraHub<>)); t = t.BaseType)
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName || m.IsGenericMethodDefinition || excluded.Contains(m.Name) || m.GetBaseDefinition().DeclaringType == typeof(object)) continue;
                var name = m.GetCustomAttribute<HubMethodNameAttribute>()?.Name ?? m.Name;
                if (result.ContainsKey(name))
                {
                    if (t == hubType || result[name]._method.DeclaringType != t)
                        throw new NotSupportedException($"Hibira: duplicate hub method name '{name}' on {hubType.Name}. Overloads are not supported.");
                    continue;
                }
                result[name] = new HubMethod(m);
            }
        }
        return result;
    }

    public object?[] BindArguments(JsonElement arguments, IServiceProvider services, JsonSerializerOptions json, CancellationToken aborted)
    {
        var isService = services.GetService<IServiceProviderIsService>();
        var given = arguments.ValueKind == JsonValueKind.Array ? arguments.EnumerateArray().ToList() : new List<JsonElement>();
        var fromClient = _parameters.Count(p => !IsInjected(p, isService));
        if (given.Count != fromClient)
            throw new InvalidDataException($"Invocation provides {given.Count} argument(s) but target expects {fromClient}.");
        var args = new object?[_parameters.Length];
        var next = 0;
        for (var i = 0; i < _parameters.Length; i++)
        {
            var p = _parameters[i];
            if (p.ParameterType == typeof(CancellationToken)) args[i] = aborted;
            else if (IsInjected(p, isService)) args[i] = services.GetRequiredService(p.ParameterType);
            else args[i] = given[next++].Deserialize(p.ParameterType, json);
        }
        return args;
    }

    private static bool IsInjected(ParameterInfo p, IServiceProviderIsService? isService) =>
        p.ParameterType == typeof(CancellationToken) ||
        p.GetCustomAttributes().Any(a => a.GetType().Name == "FromServicesAttribute" || a.GetType().Name == "FromKeyedServicesAttribute") ||
        (isService != null && !p.ParameterType.IsPrimitive && p.ParameterType != typeof(string) && isService.IsService(p.ParameterType));

    public Task<object?> InvokeAsync(HibiraHub hub, object?[] args) => _await(_method.Invoke(hub, args));

    private static (Func<object?, Task<object?>>, bool) MakeAwaiter(Type t)
    {
        if (t == typeof(void)) return (_ => Task.FromResult<object?>(null), false);
        if (t == typeof(Task)) return (async r => { await ((Task)r!).ConfigureAwait(false); return null; }, false);
        if (t == typeof(ValueTask)) return (async r => { await ((ValueTask)r!).ConfigureAwait(false); return null; }, false);
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var result = t.GetProperty("Result")!;
            return (async r => { await ((Task)r!).ConfigureAwait(false); return result.GetValue(r); }, true);
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = t.GetMethod("AsTask")!;
            return (async r =>
            {
                var task = (Task)asTask.Invoke(r, null)!;
                await task.ConfigureAwait(false);
                return task.GetType().GetProperty("Result")!.GetValue(task);
            }, true);
        }
        return (r => Task.FromResult(r), true);
    }
}
