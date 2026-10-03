# Seq MCP Server

A Model Context Protocol (MCP) server that provides tools for searching and streaming events from Seq.

## Installation

### As a .NET Global Tool (Recommended)

```bash
# Install
dotnet tool install -g SeqMcpServer

# Update to latest version
dotnet tool update -g SeqMcpServer

# Uninstall
dotnet tool uninstall -g SeqMcpServer
```

### Requirements

- .NET 10.0 Runtime or SDK
- Seq server (local or remote)
- Valid Seq API key

## Quick Start

### Development Environment

```bash
# Clone the repository
git clone https://github.com/willibrandon/seq-mcp-server
cd seq-mcp-server

# Setup development environment (fully automated)
# PowerShell (Windows)
./scripts/setup-dev.ps1

# Bash (Linux/Mac)
./scripts/setup-dev.sh

# Build and run the MCP server
dotnet build
dotnet run --project SeqMcpServer
```

The setup script automatically:
- Starts a Seq container on ports 15341/18081
- Configures authentication and creates an API key
- Sets up environment variables
- Creates a `.env` file for the application

### Production Deployment

MCP servers are not run directly - they are launched by MCP clients. For production:

1. Build and deploy the executable:
```bash
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true
```

2. Configure your MCP client to use the deployed executable:
```json
{
  "mcpServers": {
    "seq": {
      "command": "/path/to/seq-mcp-server",
      "env": {
        "SEQ_SERVER_URL": "http://your-seq-server:5341",
        "SEQ_API_KEY": "your-production-api-key"
      }
    }
  }
}
```

## MCP Tools

The following tools are available through the MCP protocol:

- **`SeqSearch`** - Fetch whole events (message, properties, full exception) with a filter, date range, signal and pagination
  - Parameters:
    - `filter` (optional): Seq filter expression (default `""`, all events)
    - `count`: Number of events to return (default: 10, max: 1000). Events are often several KB each
    - `signalId` (optional): Signal ID to filter events (use `SignalList` to find IDs)
    - `fromDateUtc` (optional): Earliest date/time (ISO 8601, e.g., `"2024-01-01T00:00:00Z"`; a value without an offset is read as UTC)
    - `toDateUtc` (optional): Latest date/time (ISO 8601, e.g., `"2024-01-31T23:59:59Z"`)
    - `afterId` (optional): Event ID to search after (exclusive) - use for pagination
    - `timeoutSeconds` (optional): Timeout in seconds (1-300, default 30)
    - `workspace` (optional): Specific workspace to query
  - Returns: List of matching events, newest first
  - **Note:** For date filtering, use `fromDateUtc`/`toDateUtc` parameters instead of `@Timestamp` in the filter expression for better performance. There is no default range
  - **Pagination:** To fetch more, pass `afterId` with the ID of the last (oldest) event from the previous page
  - Example filters:
    - `""` - all events
    - `"error"` - events containing "error"
    - `@Level = "Error"` - error level events
    - `Application = "MyApp"` - events from specific application
    - `@Id = "event-…"` - one event, e.g. a sample id returned by `SeqQuery`
  - Example with date range:
    - `filter: "@Level = 'Error'", fromDateUtc: "2024-01-01T00:00:00Z", toDateUtc: "2024-01-31T23:59:59Z"`
  - Example with pagination:
    - First call: `filter: "", count: 1000` → returns events with IDs
    - Second call: `filter: "", count: 1000, afterId: "event-<last-id>"` → returns next batch

- **`SeqQuery`** - Run a read-only Seq SQL query: counts, `group by`, time buckets, distinct counts, or chosen columns
  - Parameters:
    - `query` (required): Seq SQL, e.g. `select count(*) as n, first(@Id) as id from stream where @Level = 'Error' group by @EventType order by n desc limit 20`. Always include a `limit`: Seq limits result size
    - `fromDateUtc` (optional): Range start (ISO 8601, UTC). Defaults to 24 hours before the range end; older events are excluded unless you set it. A range may span at most 31 days
    - `toDateUtc` (optional): Range end (ISO 8601, UTC). Defaults to now
    - `workspace` (optional): Specific workspace to query
  - Returns: `columns` and `rows`; a query grouped by `time(…)` returns `slices` instead (one per bucket, each with its rows). At most 200 rows in total; `truncated: true` means the result was cut, so narrow the query
  - With time grouping, `limit` counts rows across all slices: set it high enough, or later buckets are silently dropped
  - `@Timestamp` comes back as .NET ticks; select `ToIsoString(@Timestamp)` for a readable time

- **`SeqWaitForEvents`** - Wait for and capture live events from Seq (5-second timeout)
  - Parameters: 
    - `filter` (optional): Seq filter expression
    - `count`: Number of events to capture (default: 10, max: 100)
    - `workspace` (optional): Specific workspace to query
  - Returns: Snapshot of events captured during the wait period (may be empty if no events match)

- **`SignalList`** - List available signals (read-only)
  - Parameters:
    - `workspace` (optional): Specific workspace to query
  - Returns: List of signals with their definitions

- **`SeqConvertFilter`** - Convert fuzzy filter to strict filter expression
  - Parameters:
    - `fuzzyFilter` (required): Fuzzy search text (e.g., "error", "timeout")
    - `workspace` (optional): Specific workspace to query
  - Returns: Strict Seq filter expression for use in `SeqSearch`
  - Use case: Help users write correct filter expressions
  - Example: Convert "error" to a proper Seq filter expression

## Claude Desktop Integration

### Option 1: Using .NET Global Tool (Recommended)

After installing the global tool, add to your Claude Desktop configuration:

```json
{
  "mcpServers": {
    "seq": {
      "command": "seq-mcp-server",
      "env": {
        "SEQ_SERVER_URL": "http://localhost:5341",
        "SEQ_API_KEY": "your-api-key-here"
      }
    }
  }
}
```

### Option 2: Pre-built Release

Download the latest release for your platform and add to your MCP settings:

```json
{
  "mcpServers": {
    "seq": {
      "command": "C:\\\\Tools\\\\seq-mcp-server.exe",
      "args": [],
      "env": {
        "SEQ_SERVER_URL": "http://localhost:5341",
        "SEQ_API_KEY": "your-api-key-here"
      }
    }
  }
}
```

### Option 3: Build from Source

Build a single-file executable (requires .NET 10 runtime):

```bash
# Windows
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true

# macOS
dotnet publish -c Release -r osx-x64 -p:PublishSingleFile=true

# Linux
dotnet publish -c Release -r linux-x64 -p:PublishSingleFile=true
```

The executable will be in `SeqMcpServer/bin/Release/net10.0/{runtime}/publish/`

## Configuration

The Seq MCP Server uses environment variables for configuration:

- `SEQ_SERVER_URL`: URL of your Seq server
- `SEQ_API_KEY`: API key for accessing Seq (required)
- `SEQ_API_KEY_<WORKSPACE>`: Optional workspace-specific API keys (e.g., `SEQ_API_KEY_PRODUCTION`)

### Seq Compatibility

`SeqSearch` prefers `Events.EnumerateAsync()`, which uses the Seq `Scan` link when the server advertises it. Older Seq builds such as `2024.3.x` do not expose `Scan` on `api/events/resources`; in that case the server now falls back to `PagedEnumerateAsync()` so searches continue to work instead of failing with:

```text
System.NotSupportedException: The requested link `Scan` isn't available on entity `Seq.Api.Model.ResourceGroup`.
```

If you are debugging compatibility issues:

- Seq `2025.2.x` and newer expose `Scan`
- Seq `2024.3.x` does not expose `Scan`
- this MCP server supports both paths by falling back automatically

### Workspace Support

Every tool takes an optional `workspace`, which selects an API key, not a server or an environment:

```bash
export SEQ_API_KEY="default-key"
export SEQ_API_KEY_PRODUCTION="production-key"
export SEQ_API_KEY_STAGING="staging-key"
```

`workspace: "production"` uses `SEQ_API_KEY_PRODUCTION`. A workspace with no key configured is an error,
not a silent fallback to the default key. To narrow results to an environment, filter on it instead
(for example `Environment = 'PROD'`).

## Development

### Prerequisites

- .NET 10.0 SDK
- Docker (for running Seq locally)

### Running Tests

```bash
dotnet test
```

### Development

The `scripts` folder contains automated setup scripts:

- **`setup-dev.ps1` / `setup-dev.sh`**: Automatically configures your development environment
  - Starts Seq container with authentication
  - Handles initial password setup
  - Creates development API key
  - Sets environment variables
  - Creates `.env` file for the application
  
- **`teardown-dev.ps1` / `teardown-dev.sh`**: Cleans up the development environment
  - Stops and removes containers
  - Clears environment variables

For detailed development setup, see [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Architecture

This is a pure MCP server implementation that:
- Runs as a stdio-based service (no web server)
- Communicates via JSON-RPC over standard input/output
- Clears the default .NET logging providers at startup, so nothing is written to stdout/stderr (which would corrupt the JSON-RPC stream)
- Stays silent unless a `Serilog` configuration section is supplied — no diagnostic events go anywhere until you wire up a sink

### Self-Logging

The MCP server does not create a Seq logging sink automatically. `SEQ_SERVER_URL` and `SEQ_API_KEY` are the **query** credentials — they are not used for ingesting the MCP server's own diagnostics. Ingesting to Seq requires a separate **ingest** API key, configured below.

To send the MCP server's diagnostics to Seq, explicitly configure Serilog:

```json
{
  "Serilog": {
    "Using": [ "Serilog.Sinks.Seq" ],
    "MinimumLevel": "Information",
    "WriteTo": [
      {
        "Name": "Seq",
        "Args": {
          "serverUrl": "http://localhost:5341",
          "apiKey": "your-ingest-api-key"
        }
      }
    ],
    "Properties": {
      "Application": "SeqMcpServer"
    }
  }
}
```

## License

MIT License - see [LICENSE](LICENSE) file for details.
