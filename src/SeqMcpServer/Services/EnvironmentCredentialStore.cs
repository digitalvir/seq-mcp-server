using ModelContextProtocol;

namespace SeqMcpServer.Services;

public sealed class EnvironmentCredentialStore : ICredentialStore
{
    private readonly string? _defaultApiKey;

    public EnvironmentCredentialStore()
    {
        _defaultApiKey = Environment.GetEnvironmentVariable("SEQ_API_KEY");
        
        if (string.IsNullOrEmpty(_defaultApiKey))
        {
            throw new InvalidOperationException(
                "SEQ_API_KEY environment variable is not set. " +
                "Please run ./scripts/setup-dev.ps1 (or .sh) to set up your development environment, " +
                "or set SEQ_API_KEY manually.");
        }
    }

    public string GetApiKey(string workspace)
    {
        // For MCP servers, we typically use a single API key
        // If workspace-specific keys are needed, they can be set as SEQ_API_KEY_<WORKSPACE>
        if (!string.IsNullOrEmpty(workspace) && workspace != "default")
        {
            var variable = $"SEQ_API_KEY_{workspace.ToUpperInvariant()}";
            var workspaceKey = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(workspaceKey))
                return workspaceKey;

            // Falling back to the default key would return the same data as no workspace at all, so a
            // caller who meant "workspace: PROD" as a filter would wrongly believe it had filtered.
            throw new McpException(
                $"No API key is configured for workspace '{workspace}' (set {variable}). " +
                "A workspace only selects an API key, not an environment; to narrow results, use a filter " +
                "such as Environment = 'PROD'. Omit workspace to use the default key.");
        }

        return _defaultApiKey ?? throw new InvalidOperationException("API key is null");
    }

    public void Reload()
    {
        // Environment variables don't need reloading
    }
}