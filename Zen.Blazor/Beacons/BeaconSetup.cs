using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Zen.Blazor.Beacons;

public static class BeaconSetup
{
    public static IServiceCollection AddBeacons(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(BeaconOptions.Section);
        var options = section.Get<BeaconOptions>() ?? new BeaconOptions();

        services.AddOptions<BeaconOptions>()
            .Bind(section)
            .Validate(o => o.IsValid(), "Invalid Beacons settings.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<BeaconCapture>();
        services.AddSingleton<BeaconBuffer>();
        services.AddSingleton<DatabaseCircuit>();
        services.AddSingleton<BeaconService>();

        var connectionString = PostgresSettings.ConnectionString(configuration, options.MaxPoolSize);
        if (connectionString is null)
        {
            services.AddSingleton<NullBeaconStore>();
            services.AddSingleton<IBeaconStore>(sp => sp.GetRequiredService<NullBeaconStore>());
            services.AddSingleton<IBeaconMaintenance>(sp => sp.GetRequiredService<NullBeaconStore>());
        }
        else
        {
            services.AddSingleton(sp => new NpgsqlDataSourceBuilder(connectionString)
                .UseLoggerFactory(sp.GetRequiredService<ILoggerFactory>())
                .Build());
            services.AddSingleton<PostgresBeaconStore>();
            services.AddSingleton<IBeaconStore>(sp => sp.GetRequiredService<PostgresBeaconStore>());
            services.AddSingleton<IBeaconMaintenance>(sp => sp.GetRequiredService<PostgresBeaconStore>());
        }

        services.AddSingleton<BeaconJanitor>();
        services.AddHostedService(sp => sp.GetRequiredService<BeaconJanitor>());
        services.AddHostedService<BeaconWriter>();

        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, BeaconJsonContext.Default));

        services.AddCors(o => o.AddPolicy(BeaconEndpoints.CorsPolicy, policy => policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .WithMethods(HttpMethods.Get, HttpMethods.Post)
            .WithExposedHeaders("Location", "Retry-After")
            .SetPreflightMaxAge(TimeSpan.FromHours(1))));

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return new ValueTask(TypedResults
                    .Problem("Too many requests. Slow down.", statusCode: StatusCodes.Status429TooManyRequests)
                    .ExecuteAsync(context.HttpContext));
            };
            o.AddPolicy(BeaconEndpoints.WritePolicy, context => PerClient(context, options.WritesPerMinute));
            o.AddPolicy(BeaconEndpoints.ReadPolicy, context => PerClient(context, options.ReadsPerMinute));
        });

        return services;
    }

    public static WebApplication UseBeacons(this WebApplication app)
    {
        var cspKey = app.Services.GetRequiredService<IOptions<BeaconOptions>>().Value.CspKey;
        var reportPath = $"/api/beacons/{cspKey}";
        var policy = string.Join("; ",
            "default-src 'self'",
            "script-src 'self' 'unsafe-inline'",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data:",
            "connect-src 'self'",
            "font-src 'self'",
            "object-src 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "frame-ancestors 'none'",
            $"report-uri {reportPath}",
            "report-to csp");

        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                if (context.Features.Get<IStatusCodePagesFeature>() is { } statusPages)
                {
                    statusPages.Enabled = false;
                }
            }
            else
            {
                var request = context.Request;
                context.Response.Headers.ContentSecurityPolicyReportOnly = policy;
                context.Response.Headers["Reporting-Endpoints"] = $"csp=\"{request.Scheme}://{request.Host}{reportPath}\"";
            }

            return next(context);
        });

        app.UseCors();
        app.UseRateLimiter();
        return app;
    }

    private static RateLimitPartition<string> PerClient(HttpContext context, int perMinute) =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = perMinute,
                TokensPerPeriod = 1,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1) / perMinute,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
}
