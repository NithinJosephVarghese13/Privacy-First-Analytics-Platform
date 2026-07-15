using System.Text;
using System.Text.Json;

namespace PrivacyAnalytics.Api.Middleware;

/// <summary>
/// Strips the query string from the <c>url</c> field of a <c>POST /api/v1/track</c> payload before
/// any other component (model binding, handlers, hashing, queueing) touches the body. This is the
/// first line of defence against the most common PII leak vector: query-string parameters such as
/// <c>?email=</c>, <c>?token=</c>, <c>?ref=</c> and UTM tags that the client script may forward
/// verbatim. By scrubbing at the middleware boundary we guarantee that no downstream stage — present
/// or future — can ever observe the raw query string.
/// </summary>
/// <remarks>
/// <b>Scope (v1):</b> only query-string parameters are removed. PII embedded directly in path
/// segments (e.g. <c>/password-reset/user@email.com</c>) is NOT scrubbed in v1. A configurable,
/// regex-based path-pattern scrubber is planned post-MVP; this residual risk is documented in the
/// Risk Register, not hidden.
/// <para>
/// The middleware mutates the request body in place by reading it into memory, rewriting the JSON,
/// and replacing the request stream. It only does so for <c>POST /api/v1/track</c>; all other
/// requests pass through untouched. Body size is capped (<see cref="MaxBodyBytes"/>) to bound the
/// in-memory buffering cost.
/// </para>
/// </remarks>
public sealed class UrlScrubbingMiddleware
{
    private const string TrackPath = "/api/v1/track";
    private const int MaxBodyBytes = 64 * 1024;

    private static readonly JsonSerializerOptions ScrubJsonOptions = new(JsonSerializerDefaults.Web)
    {
        // Preserve unknown properties so we forward everything the client sent, only mutating `url`.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<UrlScrubbingMiddleware> _logger;

    public UrlScrubbingMiddleware(RequestDelegate next, ILogger<UrlScrubbingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;

        var isTrackPost = HttpMethods.IsPost(request.Method)
            && request.Path.Equals(TrackPath, StringComparison.OrdinalIgnoreCase);

        if (!isTrackPost)
        {
            await _next(context);
            return;
        }

        // Reject oversized payloads up front to bound the in-memory buffering cost. A missing or
        // non-numeric Content-Length (e.g. chunked transfer) is allowed through; EnableBuffering
        // below caps the memory footprint via the runtime's buffer threshold.
        if (request.ContentLength is { } declaredLength && declaredLength > MaxBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        request.EnableBuffering();

        var scrubbedJson = await ReadAndScrubAsync(context, request);
        if (scrubbedJson is null)
        {
            // Body was not JSON, was malformed, or empty — let the endpoint produce its own 400.
            await _next(context);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(scrubbedJson);
        request.Body = new MemoryStream(bytes);
        request.Headers.ContentLength = bytes.Length;
        request.Body.Position = 0;

        await _next(context);
    }

    private async Task<string?> ReadAndScrubAsync(HttpContext context, HttpRequest request)
    {
        string? raw;
        using (var reader = new StreamReader(
                   request.Body,
                   Encoding.UTF8,
                   detectEncodingFromByteOrderMarks: false,
                   bufferSize: 4096,
                   leaveOpen: true))
        {
            raw = await reader.ReadToEndAsync();
        }

        request.Body.Position = 0;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // JsonElement values point into JsonDocument's pooled buffer and become invalid once the
        // document is disposed, so we materialize the rewritten string before leaving this scope.
        string rewritten;
        using (var doc = JsonDocument.Parse(raw))
        {
            if (!doc.RootElement.TryGetProperty("url", out var urlElement)
                || urlElement.ValueKind != JsonValueKind.String)
            {
                // No `url` to scrub; let the endpoint validate the payload as-is.
                return null;
            }

            var originalUrl = urlElement.GetString();
            if (string.IsNullOrEmpty(originalUrl))
            {
                return null;
            }

            var scrubbedUrl = StripQueryString(originalUrl);
            if (scrubbedUrl == originalUrl)
            {
                return null; // nothing to rewrite
            }

            // Re-serialize preserving all properties, only mutating `url`. We round-trip through a
            // dictionary to keep unknown fields intact rather than binding to a fixed DTO here.
            var root = doc.RootElement.Deserialize<Dictionary<string, JsonElement>>(ScrubJsonOptions)
                ?? new Dictionary<string, JsonElement>();
            root["url"] = JsonSerializer.SerializeToElement(scrubbedUrl);

            rewritten = JsonSerializer.Serialize(root, ScrubJsonOptions);

            _logger.LogInformation(
                "UrlScrubbing: stripped query string from track payload. Original='{Original}', Scrubbed='{Scrubbed}'",
                originalUrl,
                scrubbedUrl);
        }

        return rewritten;
    }

    /// <summary>
    /// Removes the query component (everything from the first <c>?</c>) and any trailing fragment
    /// (<c>#</c>) from the URL. The fragment is stripped because it is never transmitted to a server
    /// and retaining a client-side fragment would be a no-op, but stripping it keeps the stored path
    /// clean and avoids surprising dashboards with <c>#section</c> suffixes.
    /// </summary>
    internal static string StripQueryString(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url;
        }

        var queryIndex = url.IndexOf('?');
        var fragmentIndex = url.IndexOf('#');

        var cut = -1;
        if (queryIndex >= 0 && fragmentIndex >= 0)
        {
            cut = Math.Min(queryIndex, fragmentIndex);
        }
        else if (queryIndex >= 0)
        {
            cut = queryIndex;
        }
        else if (fragmentIndex >= 0)
        {
            cut = fragmentIndex;
        }

        return cut < 0 ? url : url[..cut];
    }
}
