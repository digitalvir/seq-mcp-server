using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SeqMcpServer.Services;
using Serilog;
using Serilog.Settings.Configuration;
using System.Reflection;

// Handle command-line arguments
if (args.Length > 0)
{
    var firstArg = args[0].ToLowerInvariant();
    
    // Only handle our specific command-line options
    if (firstArg == "--version" || firstArg == "-v")
    {
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
        var version = assemblyVersion != null 
            ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}"
            : "0.0.0";
        Console.WriteLine($"seq-mcp-server {version}");
        return 0;
    }
    else if (firstArg == "--help" || firstArg == "-h" || firstArg == "-?")
    {
        Console.WriteLine("Seq MCP Server - Query Seq logs via Model Context Protocol");
        Console.WriteLine();
        Console.WriteLine("Usage: seq-mcp-server [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --version, -v    Show version information");
        Console.WriteLine("  --help, -h       Show this help message");
        Console.WriteLine();
        Console.WriteLine("Environment Variables:");
        Console.WriteLine("  SEQ_SERVER_URL   URL of your Seq server (required)");
        Console.WriteLine("  SEQ_API_KEY      API key for accessing Seq (required)");
        Console.WriteLine();
        Console.WriteLine("This is an MCP server designed to be launched by MCP clients.");
        Console.WriteLine("Configure it in your MCP client (e.g., Claude Desktop) settings.");
        return 0;
    }
    // If it's not one of our options, let it pass through to the host builder
    // This allows configuration arguments like --Seq:ServerUrl to work
}

// Load a development .env (written by scripts/setup-dev) from next to the build output only.
// Never from the working directory: an MCP client starts this server in whatever project it has
// open, and a .env there could point the server, and its API key, at another host.
string? envPath = null;
{
    var baseDir = AppContext.BaseDirectory;
    var possiblePaths = new[]
    {
        Path.Combine(baseDir, ".env"),
        Path.Combine(baseDir, "..", "..", "..", ".env"),
        Path.Combine(baseDir, "..", "..", "..", "..", ".env"),
        Path.Combine(baseDir, "..", "..", "..", "..", "..", ".env")
    };
    
    foreach (var path in possiblePaths)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            envPath = fullPath;
            break;
        }
    }
}

// Load the .env file if found
if (envPath != null && File.Exists(envPath))
{
    foreach (var line in File.ReadAllLines(envPath))
    {
        if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith('#') && line.Contains('='))
        {
            var parts = line.Split('=', 2);
            var name = parts[0].Trim();
            // Only fill gaps: a value set by the MCP client's configuration always wins.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            {
                Environment.SetEnvironmentVariable(name, parts[1].Trim());
            }
        }
    }
}

// Create host builder for the MCP server
var builder = Host.CreateApplicationBuilder(args);

// Clear all default logging providers to prevent console output
// MCP servers must not write to stdout/stderr as it interferes with JSON-RPC communication
builder.Logging.ClearProviders();

// Add filter for MCP-specific events as recommended by Microsoft
builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Information);

// Configuration validation - ensure required settings are present
var seqServerUrl = Environment.GetEnvironmentVariable("SEQ_SERVER_URL") 
    ?? builder.Configuration["Seq:ServerUrl"];
    
if (string.IsNullOrEmpty(seqServerUrl))
{
    throw new InvalidOperationException("SEQ_SERVER_URL environment variable or Seq:ServerUrl configuration must be set");
}

var seqApiKey = Environment.GetEnvironmentVariable("SEQ_API_KEY");

// Validate API key is provided
if (string.IsNullOrEmpty(seqApiKey))
{
    throw new InvalidOperationException("SEQ_API_KEY environment variable must be set");
}

if (builder.Configuration.GetSection("Serilog").Exists())
{
    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(
            builder.Configuration,
            new ConfigurationReaderOptions
            {
                SectionName = "Serilog"
            })
        .CreateLogger();

    builder.Logging.AddSerilog();
}

// Register services
builder.Services.AddSingleton<ICredentialStore, EnvironmentCredentialStore>();
builder.Services.AddSingleton<SeqConnectionFactory>();

// Configure MCP server with stdio transport
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

// Seq version validation on startup (without logging since we can't write to console)
var minVer = Version.Parse(builder.Configuration["SeqVersion:Min"] ?? "2024.1");
var maxVer = Version.Parse(builder.Configuration["SeqVersion:Max"] ?? "2025.2");

var logger = host.Services.GetService<ILogger<Program>>();

var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStarted.Register(async () =>
{
    logger?.LogInformation("Seq MCP Server started");
    
    try
    {
        var conn = host.Services.GetRequiredService<SeqConnectionFactory>().Create();
        var root = await conn.Client.GetRootAsync();
        var ver = Version.Parse(root.Version);
        
        logger?.LogInformation("Connected to Seq version {Version}", ver);
        
        if (ver < minVer || ver > maxVer)
        {
            logger?.LogWarning("Seq version {Version} is outside supported range {MinVersion}-{MaxVersion}", 
                ver, minVer, maxVer);
        }
    }
    catch (Exception ex)
    { 
        logger?.LogError(ex, "Failed to connect to Seq server");
    }
});

try
{
    // Run the MCP server
    await host.RunAsync();
    return 0;
}
finally
{
    // Ensure all logs are flushed before exit
    await Log.CloseAndFlushAsync();
}

public partial class Program { }
