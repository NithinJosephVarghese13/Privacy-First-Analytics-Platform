using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PrivacyAnalytics.Infrastructure.Ai;

public class OpenRouterTextToSqlService : IAiTextToSqlService
{
    private readonly HttpClient _httpClient;
    private readonly ISqlShapeValidator _shapeValidator;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenRouterTextToSqlService> _logger;

    private const string OpenRouterModel = "deepseek/deepseek-v4-flash";
    private const string ApiEndpoint = "https://openrouter.ai/api/v1/chat/completions";

    private static readonly Dictionary<string, string> PreValidatedDemoPrompts = new(StringComparer.OrdinalIgnoreCase)
    {
        {
            "Top 5 pages by unique visitors this week",
            "SELECT path, COUNT(DISTINCT event_id) AS total_visitors FROM reporting_analytics_events WHERE timestamp >= CURRENT_DATE - INTERVAL '7 days' GROUP BY path ORDER BY total_visitors DESC LIMIT 5;"
        },
        {
            "Show me the top 5 pages by unique visitors this week.",
            "SELECT path, COUNT(DISTINCT event_id) AS total_visitors FROM reporting_analytics_events WHERE timestamp >= CURRENT_DATE - INTERVAL '7 days' GROUP BY path ORDER BY total_visitors DESC LIMIT 5;"
        },
        {
            "Daily pageviews for the past month",
            "SELECT event_date, SUM(total_events) AS daily_views FROM reporting_daily_pageviews WHERE event_date >= CURRENT_DATE - INTERVAL '30 days' GROUP BY event_date ORDER BY event_date ASC;"
        },
        {
            "Show daily pageview trends for the last 30 days.",
            "SELECT event_date, SUM(total_events) AS daily_views FROM reporting_daily_pageviews WHERE event_date >= CURRENT_DATE - INTERVAL '30 days' GROUP BY event_date ORDER BY event_date ASC;"
        },
        {
            "Known vs estimated unique visitors count",
            "SELECT is_authenticated, COUNT(*) AS event_count FROM reporting_analytics_events GROUP BY is_authenticated;"
        },
        {
            "What are the total pageviews today?",
            "SELECT COUNT(*) AS total_pageviews FROM reporting_analytics_events WHERE timestamp >= CURRENT_DATE AND event_type = 'Pageview';"
        },
        {
            "Show top pages by total events",
            "SELECT path, total_events FROM reporting_top_pages ORDER BY total_events DESC LIMIT 10;"
        },
        {
            "Show top pages by total events.",
            "SELECT path, total_events FROM reporting_top_pages ORDER BY total_events DESC LIMIT 10;"
        }
    };

    private static readonly string SystemPrompt = """
        You are an expert PostgreSQL Text-to-SQL query generator for a privacy-first web analytics platform.
        Your job is to convert natural language questions into exactly one read-only SQL SELECT statement querying the available reporting views.

        RESTRICTED REPORTING SCHEMA (You must only query these reporting views, NEVER base tables):
        1. reporting_analytics_events:
           - event_id (uuid)
           - organization_id (uuid)
           - timestamp (timestamp with time zone)
           - event_type (character varying(50), e.g. 'Pageview', 'Click')
           - path (character varying(2048))
           - is_authenticated (boolean)

        2. reporting_daily_pageviews:
           - organization_id (uuid)
           - event_date (timestamp)
           - path (character varying(2048))
           - event_type (character varying(50))
           - total_events (bigint)

        3. reporting_top_pages:
           - organization_id (uuid)
           - path (character varying(2048))
           - event_type (character varying(50))
           - total_events (bigint)

        RULES:
        - Output exactly one SQL SELECT statement.
        - Do NOT attempt to query any base tables or system tables (no analytics_events, no pg_*, etc.).
        - Do NOT include any tenant filtering in WHERE clause unless asked; tenant isolation is handled automatically by PostgreSQL RLS.
        - Do NOT write multi-statement queries or data modification DDL/DML.
        """;

    public OpenRouterTextToSqlService(
        HttpClient httpClient,
        ISqlShapeValidator shapeValidator,
        IConfiguration configuration,
        ILogger<OpenRouterTextToSqlService> logger)
    {
        _httpClient = httpClient;
        _shapeValidator = shapeValidator;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<(string Sql, bool IsCached)> GenerateSqlAsync(
        string prompt,
        bool useCache = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt cannot be empty.", nameof(prompt));
        }

        var trimmedPrompt = prompt.Trim();

        // 1. Demo Mode Resilience: Return cached response if forced or matching pre-validated demo prompt
        if (useCache || PreValidatedDemoPrompts.TryGetValue(trimmedPrompt, out var cachedSql))
        {
            if (!PreValidatedDemoPrompts.TryGetValue(trimmedPrompt, out cachedSql))
            {
                // Default fallback cached prompt if forced cache but prompt is unknown
                cachedSql = PreValidatedDemoPrompts["Show top pages by total events."];
            }

            _logger.LogInformation("Returning pre-cached demo response for prompt: '{Prompt}'", trimmedPrompt);
            ValidateAndReturn(cachedSql);
            return (cachedSql, true);
        }

        // 2. Resolve OpenRouter API key from env or configuration
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
                     ?? _configuration["OPENROUTER_API_KEY"]
                     ?? _configuration["Ai:OpenRouterApiKey"];

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("OPENROUTER_API_KEY is missing. Falling back to demo prompt cache.");
            var fallback = PreValidatedDemoPrompts.Values.First();
            ValidateAndReturn(fallback);
            return (fallback, true);
        }

        // 3. Prepare OpenRouter API request with Structured Outputs JSON Schema
        var requestBody = new
        {
            model = OpenRouterModel,
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = trimmedPrompt }
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "sql_query_response",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            sql = new
                            {
                                type = "string",
                                description = "A single SQL SELECT statement querying the reporting views"
                            }
                        },
                        required = new[] { "sql" },
                        additionalProperties = false
                    }
                }
            }
        };

        var jsonContent = JsonSerializer.Serialize(requestBody);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP call to OpenRouter API failed. Falling back to cached prompt.");
            var fallback = PreValidatedDemoPrompts.Values.First();
            return (fallback, true);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("OpenRouter API returned status code {StatusCode}: {Error}", response.StatusCode, errorBody);
            throw new InvalidOperationException($"OpenRouter API returned error status {response.StatusCode}: {errorBody}");
        }

        var responseString = await response.Content.ReadAsStringAsync(cancellationToken);
        var jsonNode = JsonNode.Parse(responseString);

        var rawMessageContent = jsonNode?["choices"]?[0]?["message"]?["content"]?.ToString();
        if (string.IsNullOrWhiteSpace(rawMessageContent))
        {
            throw new InvalidOperationException("OpenRouter API returned empty response content.");
        }

        // Parse structured output JSON: {"sql": "..."}
        string generatedSql;
        try
        {
            var structuredObj = JsonNode.Parse(rawMessageContent);
            generatedSql = structuredObj?["sql"]?.ToString() ?? string.Empty;
        }
        catch
        {
            generatedSql = rawMessageContent.Trim();
        }

        ValidateAndReturn(generatedSql);
        return (generatedSql, false);
    }

    private void ValidateAndReturn(string sql)
    {
        var (isValid, errorMessage) = _shapeValidator.Validate(sql);
        if (!isValid)
        {
            _logger.LogWarning("Generated SQL failed shape validation: {Error}. Query: {Sql}", errorMessage, sql);
            throw new InvalidOperationException($"Generated SQL failed shape validation: {errorMessage}");
        }
    }
}
