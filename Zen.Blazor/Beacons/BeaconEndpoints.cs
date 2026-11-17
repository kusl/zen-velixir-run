using System.Globalization;

namespace Zen.Blazor.Beacons;

public static class BeaconEndpoints
{
    public const string CorsPolicy = "beacons";
    public const string WritePolicy = "beacon-write";
    public const string ReadPolicy = "beacon-read";

    public static IEndpointRouteBuilder MapBeaconEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .RequireCors(CorsPolicy)
            .AddEndpointFilter(GuardAsync);

        api.MapPost("/beacons/{key}", PostAsync).RequireRateLimiting(WritePolicy).DisableAntiforgery();
        api.MapGet("/beacons", TopKeysAsync).RequireRateLimiting(ReadPolicy);
        api.MapGet("/beacons/{key}", SummarizeAsync).RequireRateLimiting(ReadPolicy);
        api.MapGet("/beacons/{key}/entries", ListAsync).RequireRateLimiting(ReadPolicy);
        api.MapGet("/entries/{id:guid}", GetAsync).RequireRateLimiting(ReadPolicy);
        api.MapGet("/status", (BeaconService beacons) => TypedResults.Ok(beacons.Status())).RequireRateLimiting(ReadPolicy);

        return app;
    }

    private static async Task<IResult> PostAsync(
        string key, HttpContext context, BeaconCapture capture, BeaconService beacons, CancellationToken cancellationToken)
    {
        if (!BeaconKey.IsValid(key))
        {
            return InvalidKey();
        }

        var entry = await capture.FromRequestAsync(context, key, cancellationToken);
        if (entry is null)
        {
            return TypedResults.Problem($"Body exceeds {capture.MaxBodyBytes} bytes.", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        beacons.Accept(entry);
        var location = $"/api/entries/{entry.Id}";
        return TypedResults.Created(location, new AcceptedBeacon(entry.Id, entry.Key, entry.ReceivedAt, entry.Size, location));
    }

    private static async Task<IResult> GetAsync(
        Guid id, HttpContext context, BeaconService beacons, DatabaseCircuit circuit, CancellationToken cancellationToken)
    {
        var lookup = await beacons.GetAsync(id, cancellationToken);
        return lookup.Status switch
        {
            LookupStatus.Found => TypedResults.Ok(lookup.Entry),
            LookupStatus.NotFound => TypedResults.Problem("No beacon with that id. It may have expired.", statusCode: StatusCodes.Status404NotFound),
            _ => Unavailable(context, circuit),
        };
    }

    private static async Task<IResult> SummarizeAsync(
        string key, string? referer, BeaconService beacons, CancellationToken cancellationToken)
    {
        if (!BeaconKey.IsValid(key))
        {
            return InvalidKey();
        }

        return TypedResults.Ok(await beacons.SummarizeAsync(key, EmptyToNull(referer), cancellationToken));
    }

    private static async Task<IResult> ListAsync(
        string key, string? referer, Guid? before, int? limit, BeaconService beacons, CancellationToken cancellationToken)
    {
        if (!BeaconKey.IsValid(key))
        {
            return InvalidKey();
        }

        return TypedResults.Ok(await beacons.ListAsync(key, EmptyToNull(referer), before, limit ?? 50, cancellationToken));
    }

    private static async Task<IResult> TopKeysAsync(int? limit, BeaconService beacons, CancellationToken cancellationToken) =>
        TypedResults.Ok(await beacons.TopKeysAsync(limit ?? 20, cancellationToken));

    private static async ValueTask<object?> GuardAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.CacheControl = "no-store";

        try
        {
            return await next(invocation);
        }
        catch (BadHttpRequestException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: ex.StatusCode);
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(BeaconEndpoints))
                .LogError(ex, "Beacon API request failed");
            return TypedResults.Problem("Something went wrong.", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult Unavailable(HttpContext context, DatabaseCircuit circuit)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(circuit.RetryIn.TotalSeconds));
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return TypedResults.Problem(
            "Storage is temporarily unavailable and this id is not in memory. Try again shortly.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult InvalidKey() => TypedResults.Problem(
        $"Key must be 1-{BeaconKey.MaxLength} characters of letters, digits, '.', '_', '~', ':' or '-', starting with a letter or digit.",
        statusCode: StatusCodes.Status400BadRequest);

    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
