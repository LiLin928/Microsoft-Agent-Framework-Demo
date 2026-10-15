using MafAgent.Api.Agents;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.AI;

namespace MafAgent.Api.Mcp;

/// <summary>
/// MCP 客户端接入（迁移自 SK8 的 MCP 插件区域）：
/// SK8: McpClientFactory.CreateAsync + ListToolsAsync + ToKernelFunction（手工转 KernelFunction）
/// MAF: McpClient.CreateAsync + ListToolsAsync —— McpClientTool 本身实现了 AIFunction，可直接作为 Agent 工具。
/// 支持 Stdio / SSE / StreamableHttp 三种传输。
/// </summary>
public static class McpToolLoader
{
    /// <summary>加载一个 MCP 服务的全部工具。返回工具列表与需要随请求生命周期释放的资源。</summary>
    public static async Task<(IReadOnlyList<AIFunction> Tools, McpClient Client)> LoadAsync(
        string serverName, McpConfig config, CancellationToken ct = default)
    {
        IClientTransport transport;

        if (!string.IsNullOrWhiteSpace(config.command))
        {
            // ===== Stdio 子进程 =====
            var stdioOptions = new StdioClientTransportOptions
            {
                Name = serverName,
                Command = config.command,
                Arguments = config.args,
                WorkingDirectory = config.WorkingDirectory,
            };
            if (config.env is { Count: > 0 })
            {
                stdioOptions.EnvironmentVariables = new Dictionary<string, string?>(config.env);
            }
            transport = new StdioClientTransport(stdioOptions);
        }
        else if (!string.IsNullOrWhiteSpace(config.url))
        {
            // ===== SSE / StreamableHttp =====
            var mode = (config.transportMode ?? "sse").ToLowerInvariant() switch
            {
                "streamablehttp" or "http" => HttpTransportMode.StreamableHttp,
                "autodetect" => HttpTransportMode.AutoDetect,
                _ => HttpTransportMode.Sse,
            };
            var httpOptions = new HttpClientTransportOptions
            {
                Name = serverName,
                Endpoint = new Uri(config.url),
                TransportMode = mode,
            };
            transport = new HttpClientTransport(httpOptions);
        }
        else
        {
            throw new ArgumentException($"MCP 服务 '{serverName}' 需要提供 command(Stdio) 或 url(SSE/Http)。");
        }

        var client = await McpClient.CreateAsync(
            transport,
            new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = $"{serverName}McpClient",
                    Version = "1.0.0",
                },
            },
            cancellationToken: ct);

        var tools = await client.ListToolsAsync(cancellationToken: ct);

        // McpClientTool : AIFunction —— 直接给 Agent 用，无需 SK8 时代的 ToKernelFunction 手工转换
        return (tools.ToList<AIFunction>(), client);
    }
}
