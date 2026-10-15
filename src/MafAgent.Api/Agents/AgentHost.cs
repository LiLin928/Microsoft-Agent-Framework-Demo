using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using ModelContextProtocol.Client;
using Microsoft.Agents.AI.LocalCodeAct;
using Microsoft.Extensions.AI;
using MafAgent.Api.Approvals;
using MafAgent.Api.Mcp;
using MafAgent.Api.Providers;
using MafAgent.Api.Tools;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace MafAgent.Api.Agents;

/// <summary>一次请求构建出的 Agent 运行时（含需要释放的资源）</summary>
public sealed class BuiltAgent : IAsyncDisposable
{
    /// <summary>本次请求构建出的 Agent 实例（HarnessAgent 或 ChatClientAgent）</summary>
    public AIAgent Agent { get; set; } = null!;

    /// <summary>多轮会话载体（跨请求凭 sessionId 复用，审批续跑也依赖它）</summary>
    public AgentSession Session { get; set; } = null!;

    /// <summary>由 Provider 矩阵解析出的聊天客户端（OpenAI 兼容 / Azure / OpenAI 官方）</summary>
    public IChatClient ChatClient { get; init; } = null!;

    /// <summary>是否为 Harness 长任务多步推理模式</summary>
    public bool UseHarness { get; init; }

    /// <summary>是否已注入 CodeAct 代码执行沙箱</summary>
    public bool CodeActEnabled { get; init; }

    /// <summary>本次请求创建的 MCP 客户端列表（释放时机与会话一致）</summary>
    public List<McpClient> McpClients { get; } = new();

    private LocalCodeActProvider? _codeActProvider;

    /// <summary>登记由本实例负责释放的 CodeAct 沙箱提供者</summary>
    internal void Own(LocalCodeActProvider provider) => _codeActProvider = provider;

    /// <summary>释放本次请求持有的全部资源（MCP 客户端、CodeAct 沙箱、聊天客户端）</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var c in McpClients)
        {
            try { await c.DisposeAsync(); } catch { /* 忽略释放异常 */ }
        }
        if (_codeActProvider is not null)
        {
            try { ((IDisposable)_codeActProvider).Dispose(); } catch { }
        }
        if (ChatClient is IDisposable d)
        {
            try { d.Dispose(); } catch { }
        }
    }
}

/// <summary>
/// Agent 装配中心：
/// Provider 矩阵 → IChatClient → （工具组装）→ ChatClientAgent（轻量，对标 SK8） 或 HarnessAgent（长任务多步推理）。
/// </summary>
public class AgentHost
{
    private readonly LlmProviderMatrix _providers;
    private readonly StandingApprovalStore _approvals;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AgentHost> _logger;

    /// <summary>构造函数：注入 Provider 矩阵、常驻审批存储、配置与日志</summary>
    /// <param name="providers">Provider 矩阵（负责把请求解析为 IChatClient）</param>
    /// <param name="approvals">常驻工具审批存储（"不再询问"授权与自动批准规则）</param>
    /// <param name="configuration">应用配置（AgentDefaults 段提供默认参数）</param>
    /// <param name="logger">日志</param>
    public AgentHost(LlmProviderMatrix providers, StandingApprovalStore approvals,
        IConfiguration configuration, ILogger<AgentHost> logger)
    {
        _providers = providers;
        _approvals = approvals;
        _configuration = configuration;
        _logger = logger;
    }

    private string DefaultPrompt => _configuration["AgentDefaults:systemPrompt"] ?? "你是一名热于助人的ai助手。";
    private int DefaultMaxContext => _configuration.GetValue("AgentDefaults:maxContextWindowTokens", 32768);
    private int DefaultMaxOutput => _configuration.GetValue("AgentDefaults:maxOutputTokens", 8192);
    private int DefaultMaxIterations => _configuration.GetValue("AgentDefaults:maximumIterationsPerRequest", 40);
    private bool DefaultEnableCodeAct => _configuration.GetValue("AgentDefaults:enableCodeAct", true);
    private string PythonPath => _configuration["AgentDefaults:pythonExecutable"] is { Length: > 0 } p
        ? p
        : OperatingSystem.IsWindows() ? "python" : "python3";

    /// <summary>构建本次请求的 Agent</summary>
    public async Task<BuiltAgent> BuildAsync(SendMessageDto req, CancellationToken ct = default)
    {
        var sessionId = string.IsNullOrWhiteSpace(req.sessionId) ? Guid.NewGuid().ToString("N") : req.sessionId!;
        var useHarness = req.useHarness ?? false;
        var enableCodeAct = (req.enableCodeAct ?? DefaultEnableCodeAct) && useHarness; // CodeAct 依赖 Harness 的 ContextProvider 管线
        var maxContext = req.maxContextWindowTokens ?? DefaultMaxContext;
        var maxOutput = DefaultMaxOutput;
        var maxIterations = req.maximumIterationsPerRequest ?? DefaultMaxIterations;

        // 1. Provider 矩阵 → IChatClient
        var chatClient = _providers.CreateChatClient(req);

        // 2. 工具组装
        var tools = new List<AITool>();
        tools.AddRange(CalculatorTool.CreateAll());                    // 内置运算（对标 DefaultTool）

        AIFunction? knowledgeTool = null;
        if (!string.IsNullOrWhiteSpace(req.dbid))
        {
            knowledgeTool = KnowledgeTool.Create();
            tools.Add(knowledgeTool);
        }

        foreach (var api in req.apilist)                               // OpenAPI 动态工具（对标 OpenApiKernelPluginFactory）
        {
            try
            {
                tools.AddRange(await OpenApiToolFactory.CreateFromOpenApiAsync(api.Name, api.Uri, api.Path));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenAPI 导入失败: {Name}", api.Name);
            }
        }

        var built = new BuiltAgent
        {
            ChatClient = chatClient,
            UseHarness = useHarness,
            CodeActEnabled = enableCodeAct,
            // Session 在下方赋值
            Session = null!,
            Agent = null!,
        };

        foreach (var (serverName, mcpConfig) in req.mcpServers ?? new())   // MCP 工具（对标 SK8 MCP 插件）
        {
            try
            {
                var (mcpTools, client) = await McpToolLoader.LoadAsync(serverName.Replace("-", "_"), mcpConfig, ct);
                built.McpClients.Add(client);
                tools.AddRange(mcpTools);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP 加载失败: {Server}", serverName);
            }
        }

        // 3. 组装 Agent（两种模式）
        var systemPrompt = string.IsNullOrWhiteSpace(req.prop) ? DefaultPrompt : req.prop!;
        var name = "maf-agent";

        if (useHarness)
        {
            // ============ Agent Harness：长任务多步推理 ============
            var harnessOptions = new HarnessAgentOptions
            {
                Name = name,
                Description = "MAF Harness Agent：todo 规划 / 模式机 / 文件记忆 / 压缩 / 审批",
                HarnessInstructions = "分步推进任务，先用 todos 拆解计划再逐步执行；工具结果需核实后再汇报。",
                ChatOptions = new ChatOptions
                {
                    Instructions = systemPrompt,
                    Tools = tools,
                },
                // ---- 上下文压缩（context compaction）：提供 token 预算即自动启用两阶段压缩 ----
                MaxContextWindowTokens = maxContext,
                MaxOutputTokens = maxOutput,
                // ---- 多步推理循环上限 ----
                MaximumIterationsPerRequest = maxIterations,
                // ---- 常驻工具审批（standing tool approvals）----
                // Harness 默认启用 ToolApprovalAgent（人工确认）；AutoApprovalRules 提供"不再询问"的自动批准规则
                DisableToolAutoApproval = false,
                ToolApprovalAgentOptions = new ToolApprovalAgentOptions
                {
                    // 自动批准规则：命中即免人工确认（与 StandingApprovalStore 双保险）
                    AutoApprovalRules = BuildAutoApprovalRules(req, sessionId),
                },
            };

            // ---- CodeAct 沙箱：注入 execute_code 工具（Python 子进程 + AST 校验 + 导入白名单）----
            if (enableCodeAct)
            {
                var codeAct = CreateCodeActProvider();
                harnessOptions.AIContextProviders = new List<AIContextProvider> { codeAct };
                built.Own(codeAct);
            }

            built.Agent = chatClient.AsHarnessAgent(harnessOptions);
        }
        else
        {
            // ============ 轻量 ChatClientAgent（对标 SK8 GetStreamingChatMessageContentsAsync）============
            var agentOptions = new ChatClientAgentOptions
            {
                Name = name,
                Description = "MAF ChatClientAgent：轻量对话（对标 SK8 行为）",
                ChatOptions = new ChatOptions
                {
                    Instructions = systemPrompt,
                    Tools = tools,
                },
                // ---- 上下文压缩：对标 SK8 ChatHistoryTruncationReducer(targetCount) ----
                // ContextWindowCompactionStrategy：先折叠旧工具结果，再截断最旧消息组
                AIContextProviders = new List<AIContextProvider>
                {
                    new CompactionProvider(
                        new ContextWindowCompactionStrategy(maxContext, maxOutput, 0.6, 0.8),
                        stateKey: $"compaction:{sessionId}"),
                },
            };
            built.Agent = chatClient.AsAIAgent(agentOptions);
        }

        // 4. 会话（多轮上下文载体；SK8 中靠前端回传 history，MAF 由 AgentSession 原生承载）
        built.Session = await built.Agent.CreateSessionAsync(ct);

        // 5. 注册请求级自动批准工具
        _approvals.RegisterRequestAutoApprove(sessionId, req.autoApproveTools);

        return built;
    }

    /// <summary>
    /// Harness 的自动批准规则（standing approvals / "不再询问"）。
    /// 内置安全名单 + 全局/会话级常驻授权 + 请求级指定。
    /// </summary>
    private List<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> BuildAutoApprovalRules(SendMessageDto req, string sessionId)
    {
        var rules = new List<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>();
        var session = sessionId;

        // 规则1：内置低风险工具（数学运算、知识检索、echo）
        rules.Add(ctx =>
        {
            var name = ctx.FunctionCallContent.Name;
            var safe = name is "Add" or "Subtraction" or "Multiplication" or "Division"
                or "Sqrt" or "Abs" or "Max" or "Min" or "Cos"
                or "search_knowledge" or "Echo" or "echo";
            return ValueTask.FromResult(safe);
        });

        // 规则2：StandingApprovalStore 中已授权（会话级/全局/请求级，"不再询问"的落点）
        rules.Add(ctx => ValueTask.FromResult(_approvals.ShouldAutoApprove(session, ctx.FunctionCallContent.Name)));

        return rules;
    }

    /// <summary>
    /// CodeAct 沙箱：Python 子进程执行模型生成的代码。
    /// - AST 校验 + 导入/内建白名单（默认阻止 os.system / subprocess 等危险调用）
    /// - 可挂载宿主工具（沙箱内 call_tool 调用）
    /// - 可选 FileMounts 限定文件访问
    /// </summary>
    private LocalCodeActProvider CreateCodeActProvider()
    {
        var timeout = _configuration.GetValue("AgentDefaults:codeActTimeoutSeconds", 30);
        var options = new LocalCodeActProviderOptions
        {
            ExecutionLimits = new ProcessExecutionLimits
            {
                TimeoutSeconds = timeout,
            },
            // 宿主工具注入：沙箱内的 Python 代码可以 call_tool("add", ...) 调用宿主能力
            Tools = new AIFunction[]
            {
                AIFunctionFactory.Create(
                    (int a, int b) => a + b,
                    name: "add",
                    description: "宿主工具示例：两个整数相加"),
            },
        };

        return new LocalCodeActProvider(PythonPath, options);
    }

    /// <summary>把 SK8 风格的请求历史转成 MAF ChatMessage 序列</summary>
    public static List<ChatMessage> BuildInputMessages(SendMessageDto req)
    {
        var messages = new List<ChatMessage>();

        // 历史消息（兼容 SK8：前端回传 history；正式多轮建议用 sessionId + AgentSession）
        foreach (var item in req.History)
        {
            var role = item.Type?.Equals("Assistant", StringComparison.OrdinalIgnoreCase) == true
                ? ChatRole.Assistant : ChatRole.User;
            messages.Add(new ChatMessage(role, item.content));
        }

        // 知识库检索注入（对标 SK8：dbid → selectVector → 注入上下文）
        if (!string.IsNullOrWhiteSpace(req.dbid))
        {
            try
            {
                var zsknr = DataAccess.VectorTool.selectVector(req.dbid, req.Query, 1);
                if (zsknr.issuccess)
                {
                    foreach (var item in zsknr.data)
                    {
                        messages.Add(new ChatMessage(ChatRole.User, $"{item.title}相关数据：{item.content}"));
                    }
                }
            }
            catch
            {
                // 知识库不可用不阻塞主流程（search_knowledge 工具仍可自主检索）
            }
        }

        // 强提示词（迁移自 SK8：echarts 展示 + 数学工具约束）
        messages.Add(new ChatMessage(ChatRole.User,
            "如果涉及到数字运算，你必须使用内置运算工具进行运算，不要自己心算。"));
        messages.Add(new ChatMessage(ChatRole.User,
            "如果涉及到统计信息，请使用我提供的echarts模版展示并总结数据,我已支持echarts环境，请不要询问我是否支持。模版：```echarts\n{optionjson}\n```"));

        // 用户提问
        messages.Add(new ChatMessage(ChatRole.User, req.Query));
        return messages;
    }
}
