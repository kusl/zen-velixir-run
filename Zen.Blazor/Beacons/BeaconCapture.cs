using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Unicode;
using Microsoft.Extensions.Options;

namespace Zen.Blazor.Beacons;

public sealed class BeaconCapture(IOptions<BeaconOptions> options, TimeProvider time)
{
    public const string Utf8Encoding = "utf-8";
    public const string Base64Encoding = "base64";
    public const string Redacted = "[redacted]";

    private const int MaxPathLength = 512;
    private const int MaxQueryLength = 2048;
    private const int MaxRefererLength = 512;
    private const int MaxUserAgentLength = 512;
    private const int MaxContentTypeLength = 256;
    private const int RowOverhead = 256;

    private static readonly HashSet<string> RedactedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "proxy-authorization",
        "cookie",
        "x-forwarded-for",
        "x-real-ip",
        "x-client-ip",
        "x-original-for",
        "forwarded",
        "true-client-ip",
        "cf-connecting-ip",
        "fastly-client-ip",
        "x-cluster-client-ip",
    };

    private readonly BeaconOptions _options = options.Value;

    public int MaxBodyBytes => _options.MaxBodyBytes;

    public async Task<BeaconEntry?> FromRequestAsync(HttpContext context, string key, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var limit = _options.MaxBodyBytes;
        if (request.ContentLength > limit)
        {
            return null;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(limit + 1);
        try
        {
            var total = 0;
            while (total <= limit)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(total, limit + 1 - total), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > limit)
            {
                return null;
            }

            var headers = CaptureHeaders(request.Headers);
            return Build(
                key,
                request.Method,
                request.Path.Value ?? "/",
                request.QueryString.Value ?? "",
                headers.GetValueOrDefault("referer") ?? headers.GetValueOrDefault("origin"),
                request.ContentType,
                Anonymize(context.Connection.RemoteIpAddress),
                headers.GetValueOrDefault("user-agent"),
                headers,
                buffer.AsSpan(0, total));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public BeaconEntry FromPage(string key, string pageUrl, string body)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-zen-source"] = "blazor-circuit",
        };

        return Build(key, "POST", "/beacons", "", pageUrl, "text/plain", null, null, headers, Encoding.UTF8.GetBytes(body));
    }

    private BeaconEntry Build(
        string key,
        string method,
        string path,
        string query,
        string? referer,
        string? contentType,
        string? client,
        string? userAgent,
        Dictionary<string, string> headers,
        ReadOnlySpan<byte> body)
    {
        var receivedAt = time.GetUtcNow();
        var isText = Utf8.IsValid(body) && !body.Contains((byte)0);
        var text = isText ? Encoding.UTF8.GetString(body) : Convert.ToBase64String(body);

        var cleanPath = Clean(path, MaxPathLength);
        var cleanQuery = Clean(query, MaxQueryLength);
        var cleanReferer = Clean(referer, MaxRefererLength);
        var cleanContentType = NullIfEmpty(Clean(contentType, MaxContentTypeLength));
        var cleanUserAgent = NullIfEmpty(Clean(userAgent, MaxUserAgentLength));

        var size = RowOverhead
            + Encoding.UTF8.GetByteCount(text)
            + key.Length
            + cleanPath.Length
            + cleanQuery.Length
            + cleanReferer.Length
            + (cleanContentType?.Length ?? 0)
            + (cleanUserAgent?.Length ?? 0)
            + headers.Sum(h => h.Key.Length + h.Value.Length + 6);

        return new BeaconEntry(
            Guid.CreateVersion7(receivedAt),
            key,
            receivedAt,
            Clean(method, 16),
            cleanPath,
            cleanQuery,
            cleanReferer,
            cleanContentType,
            client,
            cleanUserAgent,
            headers,
            text,
            isText ? Utf8Encoding : Base64Encoding,
            size);
    }

    private Dictionary<string, string> CaptureHeaders(IHeaderDictionary source)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in source)
        {
            if (headers.Count >= _options.MaxHeaders)
            {
                break;
            }

            var lower = Clean(name, 128).ToLowerInvariant();
            if (lower.Length == 0)
            {
                continue;
            }

            headers[lower] = RedactedHeaders.Contains(lower)
                ? Redacted
                : Clean(values.ToString(), _options.MaxHeaderValueLength);
        }

        return headers;
    }

    public static string? Anonymize(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            bytes[3] = 0;
            return $"{new IPAddress(bytes)}/24";
        }

        Array.Clear(bytes, 6, bytes.Length - 6);
        return $"{new IPAddress(bytes)}/48";
    }

    public static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var c in value)
        {
            if (builder.Length >= maxLength)
            {
                break;
            }

            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
        {
            builder.Length--;
        }

        return builder.ToString();
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
