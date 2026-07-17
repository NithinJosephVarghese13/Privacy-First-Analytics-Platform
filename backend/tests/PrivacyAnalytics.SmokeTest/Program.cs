using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace PrivacyAnalytics.SmokeTest
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Starting End-to-End Ingestion Smoke Test...");

            var organizationId = Guid.Parse("f43fd015-fc3c-4823-8a3e-480407a19cfb");
            var uniqueTestRun = Guid.NewGuid().ToString("N");
            
            // Wait for the API to be ready
            using var httpClient = new HttpClient();
            var apiUrl = "http://localhost:5115/api/v1/track";
            
            // Realistic tracking payload with query params in URL
            var payload = new
            {
                url = $"/pricing/{uniqueTestRun}?utm_source=test",
                referrer = "https://google.com/",
                eventType = "pageview"
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            // Add custom Headers for IP and User-Agent to ensure hashing is invoked
            httpClient.DefaultRequestHeaders.Add("X-Forwarded-For", "192.168.1.100");
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 SmokeTest/1.0");
            httpClient.DefaultRequestHeaders.Add("X-Tenant-Id", organizationId.ToString());
            httpClient.DefaultRequestHeaders.Add("X-Tenant-Origin", "https://example.com");

            Console.WriteLine($"Sending tracking payload: {json}");
            
            HttpResponseMessage response = null;
            try 
            {
                response = await httpClient.PostAsync(apiUrl, content);
            }
            catch(Exception ex)
            {
                Console.WriteLine($"Failed to contact API. Ensure it is running on 5115. Error: {ex.Message}");
                Environment.Exit(1);
            }

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Failed to ingest event. Status Code: {response.StatusCode}");
                Environment.Exit(1);
            }

            Console.WriteLine("Event ingested successfully (202 Accepted). Polling database...");

            // Connection string for the app role to test RLS
            var dbConnectionString = "Host=localhost;Port=5432;Database=analytics;Username=analytics_app;Password=analytics_app_dev";
            
            using var dbConnection = new NpgsqlConnection(dbConnectionString);
            await dbConnection.OpenAsync();

            int maxRetries = 10;
            int delayMs = 500;
            bool found = false;

            for (int i = 0; i < maxRetries; i++)
            {
                // Must set the RLS context first! SET applies to the session.
                await dbConnection.ExecuteAsync($"SET app.current_tenant_id = '{organizationId}'");

                // Check for the row where Path ends with our unique test run (but query params stripped)
                // Actually, the path is stripped, so let's query the latest event.
                // Wait, if the URL is "https://example.com/pricing?utm_source=test&run={uniqueTestRun}", the path gets scrubbed to "/pricing".
                // We should pass the unique string in the path to easily find it!
                // Let's modify the payload URL above and resend? Or just query the database for the newest row.
                // Wait, if it's scrubbed to "/pricing", it's hard to identify our specific row among 2000 others.
                // We will change the URL to: https://example.com/pricing/{uniqueTestRun}?utm_source=test
                
                var sql = "SELECT * FROM analytics_events WHERE path = @Path ORDER BY timestamp DESC LIMIT 1";
                var row = await dbConnection.QueryFirstOrDefaultAsync(sql, new { Path = $"/pricing/{uniqueTestRun}" });

                if (row != null)
                {
                    Console.WriteLine("\nRow found in database!");
                    Console.WriteLine($"Path: {row.path}");
                    Console.WriteLine($"AnonymousDailyHash: {row.anonymous_daily_hash}");
                    Console.WriteLine($"IsAuthenticated: {row.is_authenticated}");

                    // Assertions
                    if (row.path != $"/pricing/{uniqueTestRun}")
                    {
                        Console.WriteLine($"ASSERT FAILED: Expected path /pricing/{uniqueTestRun}, but got {row.path}");
                        Environment.Exit(1);
                    }
                    if (string.IsNullOrEmpty(row.anonymous_daily_hash))
                    {
                        Console.WriteLine("ASSERT FAILED: AnonymousDailyHash is null or empty");
                        Environment.Exit(1);
                    }
                    if (row.is_authenticated != false)
                    {
                        Console.WriteLine("ASSERT FAILED: IsAuthenticated should be false");
                        Environment.Exit(1);
                    }

                    Console.WriteLine("\nAll assertions passed! End-to-end ingestion is working.");
                    found = true;
                    break;
                }

                await Task.Delay(delayMs);
            }

            if (!found)
            {
                Console.WriteLine($"\nFailed to find event in database after {maxRetries * delayMs}ms.");
                Environment.Exit(1);
            }
        }
    }
}
