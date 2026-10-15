using MafAgent.Api.Agents;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using Azure.AI.OpenAI;

namespace MafAgent.Api.Providers;

/// <summary>Provider 类型</summary>
public enum ProviderKind
{
    /// <summary>OpenAI 兼容协议（DeepSeek / SiliconFlow / Kimi / 通义 / Ollama / vLLM ...）</summary>
    OpenAiCompatible,

    /// <summary>Azure OpenAI</summary>
    Azure,

    /// <summary>OpenAI 官方</summary>
    OpenAi
}

/// <summary>单个 Provider 配置</summary>
public class ProviderConfig
{
    /// <summary>openai-compatible / azure / openai</summary>
    public string kind { get; set; } = "openai-compatible";

    /// <summary>服务地址（Azure / OpenAI 兼容必填；OpenAI 官方可空）</summary>
    public string? endpoint { get; set; }

    /// <summary>API Key</summary>
    public string? apiKey { get; set; }

    /// <summary>模型名（Azure 为部署名）</summary>
    public string? model { get; set; }

    /// <summary>把 kind 字符串解析为 ProviderKind 枚举（未知值回退 OpenAiCompatible）。用方法而非计算属性，避免 ConfigurationBinder 绑定枚举失败</summary>
    /// <returns>Provider 类型</returns>
    public ProviderKind GetKind() => kind.ToLowerInvariant() switch
    {
        "azure" => ProviderKind.Azure,
        "openai" => ProviderKind.OpenAi,
        _ => ProviderKind.OpenAiCompatible
    };
}

/// <summary>
/// Provider 矩阵：统一把各厂商聊天服务解析为 Microsoft.Extensions.AI 的 IChatClient。
/// MAF 的 Agent / Harness 只依赖 IChatClient，因此新增厂商只需在此注册一行配置。
/// </summary>
public class LlmProviderMatrix
{
    private readonly ILogger<LlmProviderMatrix> _logger;
    private readonly Dictionary<string, ProviderConfig> _providers;

    /// <summary>
    /// 构造函数：从配置节 LlmProviders 读取全部 Provider 预设（名称 → ProviderConfig），
    /// 构建“Provider 矩阵”，供 CreateChatClient 按名称解析厂商客户端。
    /// </summary>
    /// <param name="configuration">应用配置（appsettings.json）</param>
    /// <param name="logger">日志</param>
    public LlmProviderMatrix(IConfiguration configuration, ILogger<LlmProviderMatrix> logger)
    {
        _logger = logger;
        _providers = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase);
        var section = configuration.GetSection("LlmProviders");
        foreach (var child in section.GetChildren())
        {
            var cfg = child.Get<ProviderConfig>();
            if (cfg != null)
            {
                _providers[child.Key] = cfg;
            }
        }
        _logger.LogInformation("Provider 矩阵已加载: {Providers}", string.Join(", ", _providers.Keys));
    }

    /// <summary>列出可用 Provider（供前端/运维查看）</summary>
    public IReadOnlyDictionary<string, string> List() =>
        _providers.ToDictionary(
            kv => kv.Key,
            kv => $"{kv.Value.GetKind()} | {kv.Value.endpoint ?? "(official)"} | model={kv.Value.model}");

    /// <summary>按名称解析预设 Provider</summary>
    public bool TryGet(string name, out ProviderConfig config) =>
        _providers.TryGetValue(name, out config!);

    /// <summary>
    /// 构造 IChatClient：
    /// 1) 请求显式给了 endpoint/apiKey/modelId → 动态 OpenAI 兼容客户端（完全对标 SK8 用法）
    /// 2) 否则按 providerName 查预设矩阵
    /// </summary>
    public IChatClient CreateChatClient(SendMessageDto request)
    {
        // 动态覆盖（SK8 兼容模式）
        if (!string.IsNullOrWhiteSpace(request.endpoint) &&
            !string.IsNullOrWhiteSpace(request.apiKey) &&
            !string.IsNullOrWhiteSpace(request.modelId))
        {
            return CreateOpenAiCompatible(request.endpoint, request.apiKey, request.modelId);
        }

        var name = string.IsNullOrWhiteSpace(request.providerName) ? "openai" : request.providerName!;
        if (!TryGet(name, out var cfg))
        {
            throw new InvalidOperationException(
                $"Provider '{name}' 不存在。可用: {string.Join(", ", _providers.Keys)}；或直接传 endpoint/apiKey/modelId。");
        }

        if (string.IsNullOrWhiteSpace(cfg.model))
        {
            throw new InvalidOperationException($"Provider '{name}' 缺少 model 配置。");
        }

        return cfg.GetKind() switch
        {
            ProviderKind.Azure => CreateAzure(cfg.endpoint!, cfg.apiKey!, cfg.model),
            ProviderKind.OpenAi => CreateOpenAiOfficial(cfg.apiKey!, cfg.model),
            _ => CreateOpenAiCompatible(cfg.endpoint!, cfg.apiKey!, cfg.model)
        };
    }

    /// <summary>OpenAI 兼容（自定义 endpoint）→ IChatClient</summary>
    public static IChatClient CreateOpenAiCompatible(string endpoint, string apiKey, string model)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
        var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey), options);
        return openAiClient.GetChatClient(model).AsIChatClient();
    }

    /// <summary>OpenAI 官方 → IChatClient</summary>
    public static IChatClient CreateOpenAiOfficial(string apiKey, string model)
    {
        var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey));
        return openAiClient.GetChatClient(model).AsIChatClient();
    }

    /// <summary>Azure OpenAI（endpoint 为资源地址，model 为部署名）→ IChatClient</summary>
    public static IChatClient CreateAzure(string endpoint, string apiKey, string deployment)
    {
        var azureClient = new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(apiKey));
        return azureClient.GetChatClient(deployment).AsIChatClient();
    }
}
