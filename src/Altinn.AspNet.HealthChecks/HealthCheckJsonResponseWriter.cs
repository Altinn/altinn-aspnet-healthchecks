using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Altinn.AspNet.HealthChecks;

/// <summary>
/// Writes a <see cref="HealthReport"/> as the de-facto standard HealthChecks UI JSON payload —
/// byte-identical to <c>UIResponseWriter.WriteHealthCheckUIResponse</c> from
/// <c>AspNetCore.HealthChecks.UI.Client</c> — without depending on that package. The payload is
/// consumed by the HealthChecks UI dashboard and by anything else expecting that shape.
/// </summary>
/// <remarks>
/// Used by <see cref="HealthCheckEndpointRouteBuilderExtensions.MapAltinnHealthChecks"/> for all
/// mapped endpoints. Reuse it for extra endpoints you map yourself:
/// <code>
/// app.MapHealthChecks("/custom", new HealthCheckOptions
/// {
///     ResponseWriter = HealthCheckJsonResponseWriter.WriteResponse
/// });
/// </code>
/// </remarks>
public static class HealthCheckJsonResponseWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    /// <summary>
    /// Serializes <paramref name="report"/> to the response body as HealthChecks UI JSON and
    /// sets the content type to <c>application/json</c>.
    /// </summary>
    public static Task WriteResponse(HttpContext httpContext, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(report);

        httpContext.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(
            httpContext.Response.Body, JsonReport.CreateFrom(report), SerializerOptions, httpContext.RequestAborted);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new TimeSpanAsStringConverter());
        return options;
    }

    // Mirrors UIHealthReport: property names and declaration order define the JSON shape.
    // HealthStatus already has the member names and values the format expects
    // (Unhealthy=0, Degraded=1, Healthy=2), so it is serialized directly.
    private sealed class JsonReport
    {
        public HealthStatus Status { get; init; }

        public TimeSpan TotalDuration { get; init; }

        public Dictionary<string, JsonReportEntry> Entries { get; init; } = [];

        public static JsonReport CreateFrom(HealthReport report)
        {
            var jsonReport = new JsonReport
            {
                Status = report.Status,
                TotalDuration = report.TotalDuration,
            };

            foreach (var (name, entry) in report.Entries)
            {
                jsonReport.Entries.Add(name, new JsonReportEntry
                {
                    Data = entry.Data,
                    // The format surfaces the exception message, and falls back to it as the
                    // description when no description was provided.
                    Description = entry.Description ?? entry.Exception?.Message,
                    Duration = entry.Duration,
                    Exception = entry.Exception?.Message,
                    Status = entry.Status,
                    Tags = entry.Tags,
                });
            }

            return jsonReport;
        }
    }

    private sealed class JsonReportEntry
    {
        public IReadOnlyDictionary<string, object> Data { get; init; } = null!;

        public string? Description { get; init; }

        public TimeSpan Duration { get; init; }

        public string? Exception { get; init; }

        public HealthStatus Status { get; init; }

        public IEnumerable<string>? Tags { get; init; }
    }

    // The format serializes durations as "00:00:00.0000000" strings (TimeSpan.ToString()),
    // not the ISO 8601 form System.Text.Json would produce by default.
    private sealed class TimeSpanAsStringConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            TimeSpan.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
