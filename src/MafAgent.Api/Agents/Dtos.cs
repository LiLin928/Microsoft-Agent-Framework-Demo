using System.Text.Json.Serialization;

namespace MafAgent.Api.Agents;

/// <summary>
/// 发送消息请求（字段兼容 SK8 的 SendMessageDto，并新增 MAF 能力字段）
/// </summary>
public class SendMessageDto
{
    /// <summary>Provider 矩阵中的预设名称（deepseek/siliconflow/azure/openai/ollama...）。与 endpoint/apiKey/modelId 二选一</summary>
    public string? providerName { get; set; }

    /// <summary>api地址（动态覆盖，兼容 SK8 用法）</summary>
    public string? endpoint { get; set; }

    /// <summary>api key</summary>
    public string? apiKey { get; set; }

    /// <summary>模型</summary>
    public string? modelId { get; set; }

    /// <summary>问题</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>系统提示词</summary>
    public string? prop { get; set; }

    /// <summary>知识库id（传入则检索知识库注入上下文）</summary>
    public string? dbid { get; set; }

    /// <summary>历史记录</summary>
    public List<HistoryDto> History { get; set; } = new();

    /// <summary>外部api列表（swagger.json → 工具，对标 SK8 OpenApiKernelPluginFactory）</summary>
    public List<Openapijson> apilist { get; set; } = new();

    /// <summary>MCP 服务配置（Stdio/SSE/StreamableHttp）</summary>
    public Dictionary<string, McpConfig>? mcpServers { get; set; }

    // ===================== MAF 新增能力 =====================

    /// <summary>true=使用 Agent Harness（长任务多步推理：todo/规划/压缩/审批/技能）；false=轻量 ChatClientAgent（对标 SK8 行为）</summary>
    public bool? useHarness { get; set; } = false;

    /// <summary>是否启用 CodeAct 代码沙箱（Harness 模式下注入 execute_code 工具）</summary>
    public bool? enableCodeAct { get; set; }

    /// <summary>上下文压缩 token 预算（不传用 appsettings 默认值）</summary>
    public int? maxContextWindowTokens { get; set; }

    /// <summary>Harness 单次请求最大迭代次数（模型调用轮数上限）</summary>
    public int? maximumIterationsPerRequest { get; set; }

    /// <summary>工具自动批准规则：工具名列表（standing approval "不再询问"），这些工具调用免人工确认</summary>
    public List<string>? autoApproveTools { get; set; }

    /// <summary>本次会话id（用于会话隔离与审批续跑；不传则新建）</summary>
    public string? sessionId { get; set; }
}

/// <summary>历史消息（兼容 SK8）</summary>
public class HistoryDto
{
    /// <summary>类型 Assistant：ai回复 User：用户消息</summary>
    public string Type { get; set; } = "";

    /// <summary>内容</summary>
    public string content { get; set; } = "";
}

/// <summary>外部api列表 Uri 和 Path 必填一个（兼容 SK8）</summary>
public class Openapijson
{
    /// <summary>swagger.json地址</summary>
    public string? Uri { get; set; }

    /// <summary>swagger.json文件路径</summary>
    public string? Path { get; set; }

    /// <summary>工具名称</summary>
    public string Name { get; set; } = "openapi";
}

/// <summary>MCP 配置（兼容 SK8；url=SSE/Http，command=Stdio）</summary>
public class McpConfig
{
    /// <summary>SSE / StreamableHttp 地址</summary>
    public string? url { get; set; }

    /// <summary>http 传输模式：sse（默认）/ streamablehttp / autodetect</summary>
    public string? transportMode { get; set; }

    /// <summary>Stdio 可执行命令</summary>
    public string? command { get; set; }

    public List<string>? args { get; set; } = new();

    public Dictionary<string, string>? env { get; set; }

    public string? WorkingDirectory { get; set; }
}

/// <summary>SSE 流式事件（对标 SK8 ResponseMsg，扩展事件类型）</summary>
public class StreamEvent
{
    /// <summary>事件类型：delta（文本增量）/ tool（工具调用）/ approval_request（等待人工审批）/ done（结束）/ error</summary>
    [JsonPropertyName("event")]
    public string Event { get; set; } = "delta";

    /// <summary>会话id</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    /// <summary>消息id</summary>
    [JsonPropertyName("messageid")]
    public string? MessageId { get; set; }

    /// <summary>问题</summary>
    [JsonPropertyName("query")]
    public string? Query { get; set; }

    /// <summary>累积文本内容</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>本轮文本增量</summary>
    [JsonPropertyName("delta")]
    public string? Delta { get; set; }

    /// <summary>工具调用信息</summary>
    [JsonPropertyName("tool")]
    public ToolCallInfo? Tool { get; set; }

    /// <summary>待审批信息（event=approval_request 时有效）</summary>
    [JsonPropertyName("approval")]
    public ApprovalRequestInfo? Approval { get; set; }

    /// <summary>token 消耗（event=done 时有效）</summary>
    [JsonPropertyName("usage")]
    public UsageInfo? Usage { get; set; }

    /// <summary>错误信息</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>工具调用信息</summary>
public class ToolCallInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }

    [JsonPropertyName("return")]
    public string? Return { get; set; }
}

/// <summary>待人工审批的工具调用</summary>
public class ApprovalRequestInfo
{
    [JsonPropertyName("approvalId")]
    public string ApprovalId { get; set; } = "";

    [JsonPropertyName("toolName")]
    public string ToolName { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

/// <summary>token 消耗</summary>
public class UsageInfo
{
    [JsonPropertyName("inputTokens")]
    public long? InputTokens { get; set; }

    [JsonPropertyName("outputTokens")]
    public long? OutputTokens { get; set; }

    [JsonPropertyName("totalTokens")]
    public long? TotalTokens { get; set; }
}

/// <summary>审批决策请求</summary>
public class ResolveApprovalDto
{
    /// <summary>会话id</summary>
    public string sessionId { get; set; } = "";

    /// <summary>待审批id</summary>
    public string approvalId { get; set; } = "";

    /// <summary>true=批准执行 false=拒绝执行</summary>
    public bool approved { get; set; }

    /// <summary>true=该工具后续调用不再询问（standing approval 常驻授权）</summary>
    public bool remember { get; set; }
}
