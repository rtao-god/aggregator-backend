using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.ProblemDetails;

public static class ProblemDetailsExtensions
{
    public static IServiceCollection AddOwnerProblemDetails(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
        return services;
    }

    public static IApplicationBuilder UseOwnerProblemDetails(this IApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.UseMiddleware<CorrelationMiddleware>();
        application.UseMiddleware<OwnerProblemDetailsMiddleware>();
        return application;
    }
}
