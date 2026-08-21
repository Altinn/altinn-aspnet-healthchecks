using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Net.Http.Headers;

namespace Altinn.AspNet.HealthChecks;

/// <summary>
/// Writes a <see cref="HealthReport"/> as <see cref="HealthCheckMediaTypes.Json"/>:
/// <code>
/// {
///   "status": "healthy",
///   "totalDuration": "00:00:00.0412000",
///   "entries": {
///     "postgres": {
///       "status": "healthy",
///       "duration": "00:00:00.0070000",
///       "description": "up",
///       "data": { "pool": "primary" },
///       "tags": [ "dependencies", "critical" ]
///     }
///   }
/// }
/// </code>
/// </summary>
/// <remarks>
/// Every per-entry field except <c>status</c> and <c>duration</c> is omitted when absent or
/// withheld, so what a reader sees depends on
/// <see cref="HealthReportWriteContext.DetailLevel"/> — see <see cref="HealthReportDetailLevel"/>.
/// </remarks>
public sealed class HealthReportJsonFormatter : HealthReportFormatter
{
    /// <summary>The shared instance. The formatter is stateless.</summary>
    public static HealthReportJsonFormatter Instance { get; } = new();

    // Passes non-ASCII through unescaped, so Norwegian check names and descriptions stay readable
    // in a raw response body. Anything HTML-sensitive is still escaped.
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = Encoder,
        Indented = false,
    };

    // Values in HealthReportEntry.Data are typed `object`, so anything the fast path below does not
    // recognise goes through the serializer. It must carry the same encoder as WriterOptions, or a
    // nested string would escape differently from a top-level one.
    private static readonly JsonSerializerOptions DataSerializerOptions = new()
    {
        Encoder = Encoder,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // An inner-exception chain is normally two or three deep. The cap is there so a cyclic or
    // pathological chain cannot turn a probe into an unbounded response.
    private const int MaxExceptionDepth = 5;

    private static readonly JsonEncodedText StatusName = JsonEncodedText.Encode("status");
    private static readonly JsonEncodedText TotalDurationName = JsonEncodedText.Encode("totalDuration");
    private static readonly JsonEncodedText EntriesName = JsonEncodedText.Encode("entries");
    private static readonly JsonEncodedText DurationName = JsonEncodedText.Encode("duration");
    private static readonly JsonEncodedText ExceptionName = JsonEncodedText.Encode("exception");
    private static readonly JsonEncodedText DescriptionName = JsonEncodedText.Encode("description");
    private static readonly JsonEncodedText DataName = JsonEncodedText.Encode("data");
    private static readonly JsonEncodedText TagsName = JsonEncodedText.Encode("tags");
    private static readonly JsonEncodedText MessageName = JsonEncodedText.Encode("message");
    private static readonly JsonEncodedText StackTraceName = JsonEncodedText.Encode("stackTrace");
    private static readonly JsonEncodedText InnerExceptionName = JsonEncodedText.Encode("innerException");

    private static readonly JsonEncodedText HealthyStatus = JsonEncodedText.Encode("healthy");
    private static readonly JsonEncodedText DegradedStatus = JsonEncodedText.Encode("degraded");
    private static readonly JsonEncodedText UnhealthyStatus = JsonEncodedText.Encode("unhealthy");

    private readonly IReadOnlyList<MediaTypeHeaderValue> _mediaTypes =
    [
        MediaTypeHeaderValue.Parse(HealthCheckMediaTypes.Json).CopyAsReadOnly(),
    ];

    /// <inheritdoc />
    /// <remarks>
    /// Only <see cref="HealthCheckMediaTypes.Json"/>. Listing <c>application/json</c> alongside it
    /// would be redundant: a <c>+json</c> type is a subset of <c>application/json</c>, so a client
    /// asking for either one lands here. Responses are always labelled with the versioned type,
    /// which is the point of having one.
    /// </remarks>
    public override IReadOnlyList<MediaTypeHeaderValue> MediaTypes => _mediaTypes;

    /// <inheritdoc />
    public override async Task WriteAsync(HealthReportWriteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var body = context.HttpContext.Response.BodyWriter;
        Write(body, context.Report, context.DetailLevel);

        // Utf8JsonWriter.Flush only hands the bytes to the IBufferWriter; this is what pushes them
        // to the transport, and the only point at which a disconnected client can cancel us.
        await body.FlushAsync(context.CancellationToken).ConfigureAwait(false);
    }

    // Synchronous by design: a health report is a few hundred bytes into a pipe that has not been
    // flushed yet, so there is nothing to await. Kept internal so tests can drive it from an
    // ArrayBufferWriter<byte> without a request pipeline.
    internal static void Write(IBufferWriter<byte> buffer, HealthReport report, HealthReportDetailLevel detailLevel)
    {
        using var writer = new Utf8JsonWriter(buffer, WriterOptions);

        writer.WriteStartObject();
        writer.WriteString(StatusName, ToJson(report.Status));
        WriteDuration(writer, TotalDurationName, report.TotalDuration);

        writer.WriteStartObject(EntriesName);
        foreach (var (name, entry) in report.Entries)
        {
            writer.WriteStartObject(name);
            WriteEntry(writer, entry, detailLevel);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteEntry(Utf8JsonWriter writer, HealthReportEntry entry, HealthReportDetailLevel detailLevel)
    {
        writer.WriteString(StatusName, ToJson(entry.Status));
        WriteDuration(writer, DurationName, entry.Duration);

        if (MayWriteException(entry, detailLevel))
        {
            writer.WritePropertyName(ExceptionName);
            WriteException(writer, entry.Exception!, detailLevel, depth: 0);
        }

        if (MayWriteDescription(entry, detailLevel))
        {
            writer.WriteString(DescriptionName, entry.Description);
        }

        if (MayWriteData(entry, detailLevel))
        {
            writer.WriteStartObject(DataName);
            foreach (var (key, value) in entry.Data)
            {
                writer.WritePropertyName(key);
                WriteDataValue(writer, value);
            }

            writer.WriteEndObject();
        }

        if (MayWriteTags(detailLevel) && entry.Tags is not null)
        {
            var wroteAny = false;
            foreach (var tag in entry.Tags)
            {
                if (!wroteAny)
                {
                    writer.WriteStartArray(TagsName);
                    wroteAny = true;
                }

                writer.WriteStringValue(tag);
            }

            if (wroteAny)
            {
                writer.WriteEndArray();
            }
        }
    }

    private static void WriteException(
        Utf8JsonWriter writer,
        Exception exception,
        HealthReportDetailLevel detailLevel,
        int depth)
    {
        writer.WriteStartObject();
        writer.WriteString(MessageName, exception.Message);

        if (MayWriteExceptionDetails(detailLevel))
        {
            if (exception.StackTrace is { } stackTrace)
            {
                writer.WriteString(StackTraceName, stackTrace);
            }

            if (exception.InnerException is { } inner && depth + 1 < MaxExceptionDepth)
            {
                writer.WritePropertyName(InnerExceptionName);
                WriteException(writer, inner, detailLevel, depth + 1);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteDataValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            default:
                // TValue is object on purpose: the serializer then dispatches on the runtime type,
                // which is all we know about a check's data value.
                JsonSerializer.Serialize<object>(writer, value, DataSerializerOptions);
                break;
        }
    }

    private static void WriteDuration(Utf8JsonWriter writer, JsonEncodedText name, TimeSpan value)
    {
        // "c" is the invariant round-trip form ("00:00:00.0070000"), and what TimeSpan.ToString()
        // produces anyway. Spelling it out keeps the wire format independent of that default.
        Span<char> buffer = stackalloc char[32];
        if (value.TryFormat(buffer, out var written, "c", CultureInfo.InvariantCulture))
        {
            writer.WriteString(name, buffer[..written]);
        }
        else
        {
            writer.WriteString(name, value.ToString("c", CultureInfo.InvariantCulture));
        }
    }

    // A response writer must never throw, so an out-of-range status degrades to the safest reading
    // rather than failing the request that was trying to tell us something was wrong.
    private static JsonEncodedText ToJson(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => HealthyStatus,
        HealthStatus.Degraded => DegradedStatus,
        _ => UnhealthyStatus,
    };
}
