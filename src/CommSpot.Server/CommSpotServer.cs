using System.Net;
using CommSpot.Protocol;
using Microsoft.AspNetCore.HttpOverrides;

namespace CommSpot.Server;

public static class CommSpotServer
{
    /// <summary>Builds the app. Tests pass overrides through <paramref name="configure"/>.</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddEnvironmentVariables("COMMSPOT_");
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 64 * 1024;
            k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
        });
        configure?.Invoke(builder);

        builder.Services.AddOptions<ServerOptions>()
            .Bind(builder.Configuration)
            .Validate(o => o.AccessKey.Length >= ServerOptions.MinAccessKeyLength,
                $"COMMSPOT_AccessKey must be at least {ServerOptions.MinAccessKeyLength} characters.")
            .Validate(o => o.VoicePort is >= 0 and <= 65535, "VoicePort out of range.")
            .ValidateOnStart();

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<Room>();
        builder.Services.AddSingleton<JoinLimiter>();
        builder.Services.AddSingleton<UdpRelay>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<UdpRelay>());
        builder.Services.AddSignalR(o =>
        {
            o.MaximumReceiveMessageSize = 32 * 1024;
            o.EnableDetailedErrors = false;
            o.MaximumParallelInvocationsPerClient = 1;
        });
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            // Only nginx on this box may tell us the client IP (it already resolved Cloudflare's CF-Connecting-IP).
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownProxies.Clear();
            o.KnownProxies.Add(IPAddress.Loopback);
            o.KnownProxies.Add(IPAddress.IPv6Loopback);
        });

        var app = builder.Build();
        app.UseForwardedHeaders();
        app.MapGet("/api/health", () => Results.Text("ok"));
        app.MapHub<VoiceHub>(HubNames.Path);
        return app;
    }
}
