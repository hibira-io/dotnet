using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Hibira.AspNetCore;

/// <summary>Returned by AddHibira to configure more.</summary>
public interface IHibiraBuilder
{
    /// <summary>The services.</summary>
    IServiceCollection Services { get; }
}

internal sealed class HibiraBuilder : IHibiraBuilder
{
    public HibiraBuilder(IServiceCollection services) => Services = services;
    public IServiceCollection Services { get; }
}

/// <summary>Hibira Live Chat settings, from the "Hibira:LiveChat" configuration section.</summary>
public sealed class HibiraLiveChatOptions
{
    /// <summary>The workspace ID, from Live Chat › Settings › Widget (HIBIRA__LIVECHAT__WORKSPACE).</summary>
    public string? Workspace { get; set; }

    /// <summary>The Verified visitors identity secret (HIBIRA__LIVECHAT__IDENTITYSECRET).</summary>
    public string? IdentitySecret { get; set; }
}

/// <summary>Registers Hibira.</summary>
public static class HibiraServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IHibiraClient"/>, hubs and IHubContext, reading the
    /// app ID, key, secret and host from the "Hibira" configuration section.
    /// </summary>
    public static IHibiraBuilder AddHibira(this IServiceCollection services, Action<HibiraOptions>? configure = null)
    {
        services.AddOptions<HibiraOptions>().BindConfiguration(HibiraOptions.SectionName);
        if (configure != null) services.Configure(configure);
        return AddCore(services);
    }

    /// <summary>Registers Hibira with options from <paramref name="section"/>.</summary>
    public static IHibiraBuilder AddHibira(this IServiceCollection services, IConfiguration section)
    {
        services.AddOptions<HibiraOptions>().Bind(section);
        return AddCore(services);
    }

    /// <summary>Configures hub options.</summary>
    public static IHibiraBuilder AddHubOptions(this IHibiraBuilder builder, Action<HibiraHubOptions> configure)
    {
        builder.Services.Configure(configure);
        return builder;
    }

    /// <summary>
    /// Keeps hub connections and groups in the registered IDistributedCache
    /// (for example AddStackExchangeRedisCache), for apps on several servers.
    /// </summary>
    public static IHibiraBuilder AddDistributedConnectionStore(this IHibiraBuilder builder)
    {
        builder.Services.Replace(ServiceDescriptor.Singleton<IHibiraConnectionStore, DistributedHibiraConnectionStore>());
        return builder;
    }

    private static IHibiraBuilder AddCore(IServiceCollection services)
    {
        services.AddOptions<HibiraHubOptions>();
        services.AddOptions<HibiraWaitingRoomOptions>().BindConfiguration(HibiraOptions.SectionName + ":WaitingRoom");
        services.AddHttpClient("Hibira");
        services.TryAddSingleton<IHibiraClient>(sp =>
            new HibiraClient(sp.GetRequiredService<IOptions<HibiraOptions>>().Value,
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("Hibira")));
        services.AddOptions<HibiraLiveChatOptions>().BindConfiguration(HibiraOptions.SectionName + ":LiveChat");
        services.TryAddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<HibiraLiveChatOptions>>().Value;
            return new HibiraLiveChat(o.Workspace, o.IdentitySecret);
        });
        services.TryAddSingleton<IHibiraConnectionStore, MemoryHibiraConnectionStore>();
        services.TryAddSingleton<HubRegistry>();
        services.TryAddSingleton(typeof(IHubContext<>), typeof(HubContext<>));
        services.TryAddSingleton(typeof(IHubContext<,>), typeof(HubContext<,>));
        return new HibiraBuilder(services);
    }
}
