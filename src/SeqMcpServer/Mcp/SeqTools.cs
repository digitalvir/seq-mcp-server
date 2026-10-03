using ModelContextProtocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using Seq.Api.Client;
using Seq.Api.Model.Data;
using Seq.Api.Model.Events;
using Seq.Api.Model.Shared;
using Seq.Api.Model.Signals;
using SeqMcpServer.Services;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SeqMcpServer.Mcp;

/// <summary>
/// Result of converting a fuzzy filter expression to a strict Seq filter expression.
/// </summary>
/// <param name="StrictExpression">The strict filter expression (the original input if no conversion was needed).</param>
/// <param name="MatchedAsText">True if Seq interpreted the input as a free-text search rather than a filter expression.</param>
/// <param name="ReasonIfMatchedAsText">Explanation of why the input was interpreted as text, or null when it wasn't.</param>
public sealed record SeqConvertFilterResult(
    [property: JsonPropertyName("strictExpression")] string StrictExpression,
    [property: JsonPropertyName("matchedAsText")] bool MatchedAsText,
    [property: JsonPropertyName("reasonIfMatchedAsText"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ReasonIfMatchedAsText);

/// <summary>
/// Result of a Seq SQL query. A plain query fills <see cref="Rows"/>; a query grouped by time fills
/// <see cref="Slices"/>, or <see cref="Series"/> when it also groups by other columns.
/// </summary>
public sealed record SeqQueryResult(
    [property: JsonPropertyName("columns")] string[] Columns,
    [property: JsonPropertyName("rows"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<object?[]>? Rows,
    [property: JsonPropertyName("slices"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<SeqQuerySlice>? Slices,
    [property: JsonPropertyName("series"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<SeqQuerySeries>? Series,
    [property: JsonPropertyName("truncated")] bool Truncated);

public sealed record SeqQuerySlice(
    [property: JsonPropertyName("time")] string? Time,
    [property: JsonPropertyName("rows")] List<object?[]> Rows);

public sealed record SeqQuerySeries(
    [property: JsonPropertyName("key")] object?[] Key,
    [property: JsonPropertyName("slices")] List<SeqQuerySlice> Slices);

/// <summary>
/// MCP tools for interacting with Seq structured logging server.
/// </summary>
/// <remarks>
/// Errors are thrown as <see cref="McpException"/>: the MCP SDK passes only that exception's message
/// through to the client, and replaces any other exception's message with a generic one.
/// </remarks>
[McpServerToolType]
public static class SeqTools
{
    private const int MaxQueryRows = 200;
    private const int DefaultSearchTimeoutSeconds = 30;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxQueryRange = TimeSpan.FromDays(31);

    /// <summary>
    /// Normalize common filter patterns to Seq's expected format.
    /// </summary>
    private static string NormalizeFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Trim() == "*")
        {
            return string.Empty; // Empty string means "all events" in Seq
        }
        return filter;
    }

    private static DateTime? ParseUtc(string? value, string parameterName)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            throw new McpException($"Invalid {parameterName} format: {value}. Use ISO 8601 format (e.g., '2024-01-01T00:00:00Z'); a value without an offset is read as UTC.");
        }
        return parsed;
    }

    private static McpException TimedOut(string operation, string advice = "Narrow the date range or add a filter, then try again.") =>
        new($"{operation} timed out. {advice}");

    private static McpException Unreachable(Exception ex) =>
        new("Could not reach the Seq server. This is a connection problem, not a problem with the query or filter.", ex);

    /// <summary>
    /// Search historical events in Seq with the specified filter.
    /// </summary>
    /// <param name="fac">Factory for creating Seq connections</param>
    /// <param name="filter">Seq filter expression (e.g., "@Level = 'Error'"). Pass an empty string (the default) or "*" to return all events. Use fromDateUtc/toDateUtc for date filtering instead of @Timestamp in the filter expression for better performance.</param>
    /// <param name="count">Maximum number of events to return (1-1000)</param>
    /// <param name="signalId">Optional signal ID to filter events (use SignalList to find available signal IDs)</param>
    /// <param name="fromDateUtc">Optional earliest date/time (ISO 8601 format, e.g., '2024-01-01T00:00:00Z'). Use this instead of @Timestamp in filter for better performance.</param>
    /// <param name="toDateUtc">Optional latest date/time (ISO 8601 format, e.g., '2024-01-31T23:59:59Z'). Use this instead of @Timestamp in filter for better performance.</param>
    /// <param name="afterId">Optional event ID to search after (exclusive). Use for pagination - pass the ID of the last event from the previous search to get the next batch.</param>
    /// <param name="timeoutSeconds">Optional timeout in seconds (1-300). If not specified, uses the default cancellation token.</param>
    /// <param name="workspace">Optional workspace identifier for multi-tenant scenarios</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of matching events</returns>
    [McpServerTool, Description("Fetch whole events (message, properties, full exception; often several KB each), newest first. Count and rank with SeqQuery first, then read a few here, e.g. filter \"@Id = 'event-...'\". Empty filter or \"*\" = all events. No default time range: set fromDateUtc/toDateUtc (UTC) instead of @Timestamp in the filter. Times out after 30 seconds unless timeoutSeconds is set. Next page: afterId = the last event's Id.")]
    public static async Task<List<EventEntity>> SeqSearch(
        SeqConnectionFactory fac,
        string filter = "",
        [Range(1, 1000)] int count = 10,
        string? signalId = null,
        string? fromDateUtc = null,
        string? toDateUtc = null,
        string? afterId = null,
        [Range(1, 300)] int? timeoutSeconds = null,
        string? workspace = null,
        CancellationToken ct = default)
    {
        filter = NormalizeFilter(filter);
        var fromDate = ParseUtc(fromDateUtc, nameof(fromDateUtc));
        var toDate = ParseUtc(toDateUtc, nameof(toDateUtc));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds ?? DefaultSearchTimeoutSeconds));
        var token = timeoutCts.Token;

        try
        {
            using var conn = fac.Create(workspace);
            var events = new List<EventEntity>();

            SignalEntity? signalEntity = null;
            if (!string.IsNullOrEmpty(signalId))
            {
                signalEntity = await conn.Signals.FindAsync(signalId, cancellationToken: token)
                    ?? throw new McpException($"Signal with ID '{signalId}' not found. Use SignalList to find available signals.");
            }

            if (await SeqCapabilities.SupportsScanAsync(conn, token))
            {
                await foreach (var evt in conn.Events.EnumerateAsync(
                    unsavedSignal: signalEntity,
                    filter: filter,
                    count: count,
                    afterId: afterId,
                    fromDateUtc: fromDate,
                    toDateUtc: toDate,
                    render: true,
                    cancellationToken: token).WithCancellation(token))
                {
                    events.Add(evt);
                }
            }
            else
            {
                await foreach (var evt in conn.Events.PagedEnumerateAsync(
                    unsavedSignal: signalEntity,
                    signal: null,
                    filter: filter,
                    count: count,
                    startAtId: null,
                    afterId: afterId,
                    render: true,
                    fromDateUtc: fromDate,
                    toDateUtc: toDate,
                    shortCircuitAfter: null,
                    permalinkId: null,
                    variables: null,
                    background: false,
                    trace: false,
                    cancellationToken: token).WithCancellation(token))
                {
                    events.Add(evt);
                }
            }

            return WithJsonValues(events);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own timeout, or HttpClient's; returning [] here would read as "no matching events".
            throw TimedOut("Search");
        }
        catch (SeqApiException ex) when (ex.Message.Contains("Syntax error"))
        {
            throw new McpException($"Invalid filter expression: {ex.Message}. Use an empty string \"\" for all events, or a valid Seq filter expression like \"@Level = 'Error'\".", ex);
        }
        catch (SeqApiException ex)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (HttpRequestException ex)
        {
            throw Unreachable(ex);
        }
    }

    /// <summary>
    /// Run a Seq SQL query and return its result table.
    /// </summary>
    /// <param name="fac">Factory for creating Seq connections</param>
    /// <param name="query">Seq SQL query, e.g. "select count(*) as n from stream where @Level = 'Error' group by @EventType order by n desc limit 20"</param>
    /// <param name="fromDateUtc">Optional range start (ISO 8601). Defaults to 24 hours before the range end.</param>
    /// <param name="toDateUtc">Optional range end (ISO 8601). Defaults to now.</param>
    /// <param name="workspace">Optional workspace identifier for multi-tenant scenarios</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The result columns and rows, or time slices/series for queries grouped by time</returns>
    [McpServerTool, Description("Read-only Seq SQL over events: counts, group by, time buckets, distinct counts, chosen columns. Always end with a limit; Seq limits result size. Group errors by @EventType (not @MessageTemplate), with first(@Id) to read a sample via SeqSearch. Range: fromDateUtc..toDateUtc in UTC; toDateUtc defaults to now and fromDateUtc to 24 hours before it, so older events are excluded unless you set fromDateUtc; at most 31 days per query. group by time(...) returns slices (one per bucket, each with rows) instead of rows; its limit counts rows across all slices, so set it high enough or later buckets are silently dropped. @Timestamp comes back as .NET ticks: select ToIsoString(@Timestamp) instead. At most 200 rows; truncated=true means results were cut, so narrow the query. Example: select count(*) as n, first(@Id) as id from stream where @Level = 'Error' group by @EventType order by n desc limit 20")]
    public static async Task<SeqQueryResult> SeqQuery(
        SeqConnectionFactory fac,
        [Required] string query,
        string? fromDateUtc = null,
        string? toDateUtc = null,
        string? workspace = null,
        CancellationToken ct = default)
    {
        var rangeEnd = ParseUtc(toDateUtc, nameof(toDateUtc)) ?? DateTime.UtcNow;
        var rangeStart = ParseUtc(fromDateUtc, nameof(fromDateUtc)) ?? rangeEnd.AddHours(-24);
        if (rangeStart >= rangeEnd)
        {
            throw new McpException("fromDateUtc must be earlier than toDateUtc.");
        }
        if (rangeEnd - rangeStart > MaxQueryRange)
        {
            throw new McpException($"The range is {(rangeEnd - rangeStart).TotalDays:0.#} days; the maximum is {MaxQueryRange.TotalDays:0} days. Split it into several queries.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // A little longer than Seq's own timeout, so Seq's error wins when it is the one that stops.
        timeoutCts.CancelAfter(QueryTimeout + TimeSpan.FromSeconds(10));

        QueryResultPart result;
        try
        {
            using var conn = fac.Create(workspace);
            result = await conn.Data.QueryAsync(
                query,
                rangeStartUtc: rangeStart,
                rangeEndUtc: rangeEnd,
                timeout: QueryTimeout,
                cancellationToken: timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw TimedOut("Query");
        }
        catch (SeqApiException ex) when (ex.Message.Contains("could not be executed"))
        {
            // Seq.Api drops the reasons Seq sends with this error, so name the usual causes.
            throw new McpException($"{ex.Message} Usual causes: no limit clause (Seq refuses unbounded results), a syntax error, or a misspelled function.", ex);
        }
        catch (SeqApiException ex)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (HttpRequestException ex)
        {
            throw Unreachable(ex);
        }

        if (!string.IsNullOrEmpty(result.Error))
        {
            var detail = string.Join(" ", new[] { result.Error }
                .Concat(result.Reasons ?? [])
                .Append(result.Suggestion is { Length: > 0 } s ? $"Suggestion: {s}" : null)
                .Where(part => !string.IsNullOrEmpty(part)));
            throw new McpException($"Query failed: {detail}");
        }

        var remaining = MaxQueryRows;
        var truncated = false;

        List<object?[]> TakeRows(object[][]? rows)
        {
            var taken = new List<object?[]>();
            foreach (var row in rows ?? [])
            {
                if (remaining == 0)
                {
                    truncated = true;
                    break;
                }
                taken.Add(row.Select(ToJsonValue).ToArray());
                remaining--;
            }
            return taken;
        }

        // Once the cap is reached, stop if anything after it has rows: sending the rest as empty
        // slices would read as real zeros. Trailing slices that are genuinely empty still go out.
        List<SeqQuerySlice> TakeSlices(TimeSlicePart[] slices)
        {
            var taken = new List<SeqQuerySlice>();
            for (var i = 0; i < slices.Length; i++)
            {
                if (remaining == 0 && HasRows(slices.Skip(i)))
                {
                    truncated = true;
                    break;
                }
                taken.Add(new SeqQuerySlice(slices[i].Time, TakeRows(slices[i].Rows)));
            }
            return taken;
        }

        List<SeqQuerySeries> TakeSeries(TimeseriesPart[] series)
        {
            var taken = new List<SeqQuerySeries>();
            for (var i = 0; i < series.Length; i++)
            {
                if (remaining == 0 && series.Skip(i).Any(s => HasRows(s.Slices)))
                {
                    truncated = true;
                    break;
                }
                taken.Add(new SeqQuerySeries(
                    (series[i].Key ?? []).Select(ToJsonValue).ToArray(),
                    TakeSlices(series[i].Slices ?? [])));
            }
            return taken;
        }

        return new SeqQueryResult(
            result.Columns ?? [],
            result.Rows is null ? null : TakeRows(result.Rows),
            result.Slices is null ? null : TakeSlices(result.Slices),
            result.Series is null ? null : TakeSeries(result.Series),
            truncated);
    }

    private static List<EventEntity> WithJsonValues(List<EventEntity> events)
    {
        foreach (var evt in events)
        {
            evt.Properties = ConvertParts(evt.Properties);
            evt.Resource = ConvertParts(evt.Resource);
            evt.Scope = ConvertParts(evt.Scope);
#pragma warning disable CS0618 // Obsolete, Newtonsoft-only overflow data that System.Text.Json would write as junk.
            evt.ExtensionData = null;
#pragma warning restore CS0618
        }
        return events;

        static List<EventPropertyPart>? ConvertParts(List<EventPropertyPart>? parts) =>
            parts?.Select(part => new EventPropertyPart(part.Name, ToJsonValue(part.Value))).ToList();
    }

    private static bool HasRows(IEnumerable<TimeSlicePart>? slices) =>
        slices?.Any(slice => slice.Rows is { Length: > 0 }) == true;

    // Seq.Api deserializes with Newtonsoft, so structured values arrive as JToken and very large
    // integers as BigInteger; the MCP SDK serializes with System.Text.Json, which writes a JToken as
    // an empty array and a BigInteger as an object of its properties.
    private static object? ToJsonValue(object? value) => value switch
    {
        JToken token => JsonNode.Parse(token.ToString(Newtonsoft.Json.Formatting.None)),
        BigInteger big => JsonNode.Parse(big.ToString(CultureInfo.InvariantCulture)),
        _ => value,
    };

    /// <summary>
    /// Wait for and capture live events from Seq's event stream.
    /// </summary>
    /// <remarks>
    /// This method connects to Seq's live event stream and captures events as they arrive,
    /// up to the specified count or until the 5-second timeout expires. Due to MCP protocol
    /// limitations, events are returned as a complete snapshot rather than streamed incrementally.
    /// The method may return an empty list if no events matching the filter arrive within the timeout period.
    /// </remarks>
    /// <param name="fac">Factory for creating Seq connections</param>
    /// <param name="filter">Optional Seq filter expression to apply to the stream</param>
    /// <param name="count">Maximum number of events to capture (1-100)</param>
    /// <param name="workspace">Optional workspace identifier for multi-tenant scenarios</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Snapshot of events captured during the wait period (may be empty)</returns>
    [McpServerTool, Description("Wait for and capture live events from Seq (times out after 5 seconds, returns captured events as a snapshot)")]
    public static async Task<List<EventEntity>> SeqWaitForEvents(
        SeqConnectionFactory fac,
        string? filter = null,
        [Range(1, 100)] int count = 10,
        string? workspace = null,
        CancellationToken ct = default)
    {
        var events = new List<EventEntity>();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            using var conn = fac.Create(workspace);

            await foreach (var evt in conn.Events.StreamAsync(
                unsavedSignal: null,
                signal: null,
                filter: filter ?? string.Empty,
                cancellationToken: ct).WithCancellation(combinedCts.Token))
            {
                events.Add(evt);
                if (events.Count >= count) break;
            }
        }
        // Seq.Api wraps a cancelled websocket call as SeqApiException("The API call failed."), so test
        // the tokens rather than the exception type: the 5-second wait ending returns what arrived.
        catch (Exception ex) when ((ex is OperationCanceledException or SeqApiException) && combinedCts.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
        }
        catch (SeqApiException ex) when (ex.InnerException is System.Net.WebSockets.WebSocketException)
        {
            // Plain HTTP calls can still work when this fails, e.g. behind a proxy that blocks websockets.
            throw new McpException($"Seq's live event stream (a websocket) could not be opened: {ex.Message} To see recent events, use SeqSearch with fromDateUtc a few minutes ago.", ex);
        }
        catch (SeqApiException ex)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (HttpRequestException ex)
        {
            throw Unreachable(ex);
        }

        return WithJsonValues(events);
    }

    /// <summary>
    /// List available signals (saved searches) in Seq.
    /// </summary>
    /// <remarks>
    /// Signals in Seq are saved searches that can be used to quickly access commonly used filters.
    /// This method returns only shared signals (read-only access).
    /// </remarks>
    /// <param name="fac">Factory for creating Seq connections</param>
    /// <param name="workspace">Optional workspace identifier for multi-tenant scenarios</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of available signals</returns>
    [McpServerTool, Description("List available signals in Seq (read-only access to shared signals)")]
    public static async Task<List<SignalEntity>> SignalList(
        SeqConnectionFactory fac,
        string? workspace = null,
        CancellationToken ct = default)
    {
        try
        {
            using var conn = fac.Create(workspace);
            return await conn.Signals.ListAsync(shared: true, cancellationToken: ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw TimedOut("Listing signals", "Try again.");
        }
        catch (SeqApiException ex)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (HttpRequestException ex)
        {
            throw Unreachable(ex);
        }
    }

    /// <summary>
    /// Convert a fuzzy filter expression to a strict Seq filter expression.
    /// </summary>
    /// <remarks>
    /// Seq supports both "fuzzy" filters (like typing in the UI search box) and "strict" filters
    /// (formal filter expressions). This tool converts fuzzy filters to strict ones, helping users
    /// write correct filter expressions. For example, "error" becomes a proper filter expression.
    /// The result includes whether the filter was interpreted as a text search and the reason if so.
    /// </remarks>
    /// <param name="fac">Factory for creating Seq connections</param>
    /// <param name="fuzzyFilter">The fuzzy filter expression to convert (e.g., "error", "timeout")</param>
    /// <param name="workspace">Optional workspace identifier for multi-tenant scenarios</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Conversion result including the strict expression and metadata about the conversion</returns>
    [McpServerTool, Description("Convert a fuzzy filter expression to a strict Seq filter expression. Helps write correct filters for SeqSearch.")]
    public static async Task<SeqConvertFilterResult> SeqConvertFilter(
        SeqConnectionFactory fac,
        [Required] string fuzzyFilter,
        string? workspace = null,
        CancellationToken ct = default)
    {
        try
        {
            using var conn = fac.Create(workspace);
            var result = await conn.Expressions.ToStrictAsync(fuzzyFilter, cancellationToken: ct);

            if (result == null)
            {
                return new SeqConvertFilterResult(fuzzyFilter, false, null);
            }

            return new SeqConvertFilterResult(
                result.StrictExpression ?? fuzzyFilter,
                result.MatchedAsText,
                result.ReasonIfMatchedAsText);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw TimedOut("Converting the filter", "Try again.");
        }
        catch (SeqApiException ex)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (HttpRequestException ex)
        {
            throw Unreachable(ex);
        }
    }
}
