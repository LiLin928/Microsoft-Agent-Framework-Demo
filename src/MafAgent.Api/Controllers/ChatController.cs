using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using MafAgent.Api.Agents;

namespace MafAgent.Api.Controllers;

/// <summary>
/// 聊天（MAF 版，对标 SK8 ChatController.SendMessageByChatCompletion）：
/// - POST /api/Chat/SendMessage      SSE 流式对话（useHarness=true 切长任务多步推理模式）
/// - POST /api/Chat/ResolveApproval  工具审批决策（批准/拒绝 + 可选"不再询问"常驻授权）后续跑
/// - GET  /api/Chat/Providers        查看 Provider 矩阵
/// </summary>
[ApiController]
[Route("/api/[controller]/[action]")]
public class ChatController : Controller
{
    private readonly AgentHost _agentHost;
    private readonly AgentRunner _runner;
    private readonly AgentSessionRegistry _sessions;
    private readonly PendingApprovalRegistry _pending;
    private readonly Providers.LlmProviderMatrix _providers;
    private readonly ILogger<ChatController> _logger;

    /// <summary>构造函数：注入 Agent 装配、流式运行器、会话/审批注册表、Provider 矩阵与日志</summary>
    /// <param name="agentHost">Agent 装配中心（构建 Harness/轻量 Agent）</param>
    /// <param name="runner">流式运行器（SSE 事件桥接 + 审批协调）</param>
    /// <param name="sessions">会话注册表（sessionId → Agent 运行时）</param>
    /// <param name="pending">挂起审批注册表（等待人工决策的请求）</param>
    /// <param name="providers">Provider 矩阵（查看接口用）</param>
    /// <param name="logger">日志</param>
    public ChatController(
        AgentHost agentHost,
        AgentRunner runner,
        AgentSessionRegistry sessions,
        PendingApprovalRegistry pending,
        Providers.LlmProviderMatrix providers,
        ILogger<ChatController> logger)
    {
        _agentHost = agentHost;
        _runner = runner;
        _sessions = sessions;
        _pending = pending;
        _providers = providers;
        _logger = logger;
    }

    /// <summary>
    /// SSE 流式对话（响应方法）：构建 Agent 后流式输出 delta/tool/approval_request/done 事件。
    /// useHarness=true 切长任务多步推理模式；触发审批时以 approval_request 事件挂起。
    /// </summary>
    /// <param name="dto">对话请求（Provider 或动态三件套 + 提问 + 可选工具源/知识库/模式开关）</param>
    [HttpPost]
    public async Task SendMessage([FromBody] SendMessageDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Query))
        {
            Response.StatusCode = 400;
            return;
        }

        var sessionId = string.IsNullOrWhiteSpace(dto.sessionId) ? Guid.NewGuid().ToString("N") : dto.sessionId!;
        dto.sessionId = sessionId;

        // SSE 响应头
        Response.Headers.Append("Content-Type", "text/event-stream; charset=utf-8");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        BuiltAgent? built = null;
        try
        {
            built = await _agentHost.BuildAsync(dto);
            _sessions.Set(sessionId, built);

            var messages = AgentHost.BuildInputMessages(dto);

            await _runner.StreamRunAsync(built, messages, sessionId, WriteSseAsync, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "对话执行失败");
            await WriteSseAsync(new StreamEvent { Event = "error", SessionId = sessionId, Error = ex.Message });
        }
        finally
        {
            // BuiltAgent 由 AgentSessionRegistry 缓存（审批续跑要复用），此处不释放
        }
    }

    /// <summary>
    /// 工具审批决策（响应方法）：批准/拒绝该次调用并以 SSE 续跑剩余流程；
    /// remember=true 且批准时，该工具写入全局常驻授权（standing approval，后续自动放行）。
    /// </summary>
    /// <param name="dto">审批决策：sessionId + approvalId + approved（是否批准）+ remember（是否不再询问）</param>
    [HttpPost]
    public async Task ResolveApproval([FromBody] ResolveApprovalDto dto)
    {
        Response.Headers.Append("Content-Type", "text/event-stream; charset=utf-8");
        Response.Headers.Append("Cache-Control", "no-cache");

        try
        {
            if (!_pending.TryGet(dto.approvalId, out var sessionId, out var toolName, out var requestContent))
            {
                await WriteSseAsync(new StreamEvent { Event = "error", Error = $"审批请求 {dto.approvalId} 不存在或已处理。" });
                return;
            }

            if (!_sessions.TryGet(sessionId, out var built))
            {
                await WriteSseAsync(new StreamEvent { Event = "error", Error = "会话已过期，请重新发起对话。" });
                return;
            }

            // 常驻授权："不再询问"
            if (dto.remember && dto.approved)
            {
                _sessions.TryGet(sessionId, out _);
                HttpContext.RequestServices.GetRequiredService<Approvals.StandingApprovalStore>()
                    .Remember(sessionId, toolName, global: true);
            }

            _pending.Remove(dto.approvalId);

            // 构造审批响应消息并续跑
            var approvalMessage = new ChatMessage(ChatRole.User, new AIContent[] { requestContent.CreateResponse(dto.approved) });
            await _runner.StreamRunAsync(built, new[] { approvalMessage }, sessionId, WriteSseAsync, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "审批续跑失败");
            await WriteSseAsync(new StreamEvent { Event = "error", Error = ex.Message });
        }
    }

    /// <summary>查看 Provider 矩阵（响应方法）：返回全部已配置厂商及类型/地址/模型</summary>
    [HttpGet]
    public IActionResult Providers() => Ok(_providers.List());

    /// <summary>把一个流事件序列化为 JSON 并按 SSE 协议写出（data: {...}\n\n）</summary>
    /// <param name="evt">要推送的事件</param>
    private async Task WriteSseAsync(StreamEvent evt)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(evt,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        await Response.WriteAsync("data:" + json + "\n\n", HttpContext.RequestAborted);
        await Response.Body.FlushAsync(HttpContext.RequestAborted);
    }
}
