using System.Net;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ProtonBridgeProxy;

public static class ProxyHost
{
    public static WebApplication Create(Options options)
    {
        options.Validate();
        var builder = WebApplication.CreateSlimBuilder();

        // Framework exception logging can include upstream protocol data; expose only health status.
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
            server.Listen(IPAddress.Parse(options.HealthListenAddress), options.HealthPort));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ProxyListenerState>();
        builder.Services.AddHostedService<ProxyListenerService>();
        builder.Services.AddHealthChecks()
            .AddCheck<ProxyReadinessCheck>("proxy-and-bridge", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));

        var app = builder.Build();
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            // Liveness is independent of Bridge so a Bridge outage does not restart the proxy.
            Predicate = _ => false,
            ResponseWriter = WriteHealthStatusAsync
        });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready"),
            ResponseWriter = WriteHealthStatusAsync
        });
        return app;
    }

    private static Task WriteHealthStatusAsync(HttpContext context, HealthReport report)
    {
        // Never publish exception messages, paths, account names or dependency details.
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(report.Status.ToString(), context.RequestAborted);
    }
}
