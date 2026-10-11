using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UrlShortener.Application.Analytics;
using UrlShortener.Application.Links;

namespace UrlShortener.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<UrlPolicy>();
        services.AddSingleton<ClickEventFactory>();
        services.AddScoped<LinkService>();
        services.AddScoped<RedirectService>();
        services.AddScoped<StatsService>();
        return services;
    }
}
