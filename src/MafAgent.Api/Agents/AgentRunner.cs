using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MafAgent.Api.Approvals;
using MafAgent.Api.Agents;

namespace MafAgent.Api.Agents;

/// <summary>
/// 会话注册表：sessionId → BuiltAgent（跨请求复用 AgentSession，审批续跑也依赖它）。
/// 带 30 分钟滑动过期自动清理。
/// </summary>
public class AgentSessionRegistry
{
    private sealed class Entry
    {
        public BuiltAgent Agent = null!;
        public DateTimeOffset LastUsed;
    }

    private readonly ConcurrentDictionary<string, Entry> _sessions = new();
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(30);

    /// <summary>保存/覆盖一个会话的 Agent 运行时，并刷新最近使用时间</summary>
    /// <param name="sessionId">会话id（前端首次对话后保存，后续请求回传）</param>
    /// <param name="agent">本次请求构建的 Agent 运行时</param>
    public void Set(string sessionId, BuiltAgent agent)
    {
        _sessions[sessionId] = new Entry { Agent = agent, LastUsed = DateTimeOffset.UtcNow };
    }

    /// <summary>按会话id获取 Agent 运行时（命中则刷新滑动过期时间）</summary>
    /// <param name="sessionId">会话id</param>
    /// <param name="agent">输出：对应的 Agent 运行时</param>
    /// <returns>是否存在未过期的会话</returns>
    public bool TryGet(string sessionId, out BuiltAgent agent)
    {
        if (_sessions.TryGetValue(sessionId, out var e))
        {
            e.LastUsed = DateTimeOffset.UtcNow;
            agent = e.Agent;
            return true;
        }
        agent = null!;
        return false;
    }

    /// <summary>清理超过 30 分钟未使用的会话并释放其资源（由后台定时器周期调用）</summary>
    public void Cleanup()
    {
        foreach (var kv in _sessions)
        {
            if (DateTimeOffset.UtcNow - kv.Value.LastUsed > _ttl)
            {
                if (_sessions.TryRemove(kv.Key, out var removed))
                {
                    _ = removed.Agent.DisposeAsync();
                }
            }
        }
    }
}

/// <summary>挂起中的审批请求：approvalId → (sessionId, 工具名, 原始请求内容)</summary>
public class PendingApprovalRegistry
{
    private readonly ConcurrentDictionary<string, (string SessionId, string ToolName, ToolApprovalRequestContent Content)> _pending = new();

    /// <summary>登记一个等待人工决策的审批请求</summary>
    /// <param name="approvalId">审批id（即 FunctionCall.CallId，决策时回传）</param>
    /// <param name="sessionId">所属会话id</param>
    /// <param name="toolName">待审批的工具名</param>
    /// <param name="content">原始审批请求内容（续跑时据此构造响应）</param>
    public void Set(string approvalId, string sessionId, string toolName, ToolApprovalRequestContent content) =>
        _pending[approvalId] = (sessionId, toolName, content);

    /// <summary>按审批id获取挂起中的审批请求</summary>
    /// <param name="approvalId">审批id</param>
    /// <param name="sessionId">输出：所属会话id</param>
    /// <param name="toolName">输出：待审批的工具名</param>
    /// <param name="content">输出：原始审批请求内容</param>
    /// <returns>是否存在该审批请求</returns>
    public bool TryGet(string approvalId, out string sessionId, out string toolName, out ToolApprovalRequestContent content)
    {
        if (_pending.TryGetValue(approvalId, out var v))
        {
            sessionId = v.SessionId; toolName = v.ToolName; content = v.Content;
            return true;
        }
        sessionId = toolName = ""; content = null!;
        return false;
    }

    /// <summary>决策完成后移除挂起审批（防止重复处理）</summary>
    /// <param name="approvalId">审批id</param>
    /// <returns>是否成功移除</returns>
    public bool Remove(string approvalId) => _pending.TryRemove(approvalId, out _);
}

/// <summary>
/// Agent 流式运行器：把 AIAgent.RunStreamingAsync 的事件翻译为 SSE 流，
/// 并实现"常驻工具审批"的运行时协调：
///   命中 standing 规则 → 自动批准并续跑（用户无感）；
///   未命中 → 发 approval_request 事件挂起，等 /ResolveApproval 决策后续跑。
/// </summary>
public class AgentRunner
{
    private readonly StandingApprovalStore _approvals;
    private readonly PendingApprovalRegistry _pending;
    private readonly ILogger<AgentRunner> _logger;

    /// <summary>自动批准续跑的上限次数（防止规则循环）</summary>
    private const int MaxAutoApprovalRounds = 10;

    /// <summary>构造函数：注入审批存储、挂起审批注册表与日志</summary>
    /// <param name="approvals">常驻工具审批存储（standing 规则链）</param>
    /// <param name="pending">挂起审批注册表（等待人工决策的请求）</param>
    /// <param name="logger">日志</param>
    public AgentRunner(StandingApprovalStore approvals, PendingApprovalRegistry pending, ILogger<AgentRunner> logger)
    {
        _approvals = approvals;
        _pending = pending;
        _logger = logger;
    }

    /// <summary>流式运行并回调事件。返回值：是否有挂起的审批等待人工决策。</summary>
    /// <param name="built">本次请求的 Agent 运行时（含 Agent 与 Session）</param>
    /// <param name="messages">输入消息序列（历史 + 检索上下文 + 用户提问）</param>
    /// <param name="sessionId">会话id（用于审批规则判定与事件回传）</param>
    /// <param name="emit">事件回调：每个 delta/tool/approval_request/done/error 事件都会推送到 SSE</param>
    /// <param name="ct">取消令牌（客户端断开时取消）</param>
    /// <returns>true=已挂起等待人工审批；false=本轮正常结束</returns>
    public async Task<bool> StreamRunAsync(
        BuiltAgent built,
        IEnumerable<ChatMessage> messages,
        string sessionId,
        Func<StreamEvent, Task> emit,
        CancellationToken ct = default)
    {
        var messageId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var sb = new System.Text.StringBuilder();
        var tools = new List<ToolCallInfo>();
        UsageInfo? usage = null;
        var pendingApproval = false;
        var inputTokens = 0L;
        var outputTokens = 0L;
        var totalTokens = 0L;

        var input = messages.ToList();
        var rounds = 0;

        while (true)
        {
            rounds++;
            if (rounds > MaxAutoApprovalRounds)
            {
                _logger.LogWarning("自动审批续跑超过 {Max} 轮，终止", MaxAutoApprovalRounds);
                break;
            }

            ToolApprovalRequestContent? foundApproval = null;
            var newText = new System.Text.StringBuilder();

            await foreach (var update in built.Agent.RunStreamingAsync(input, built.Session, cancellationToken: ct))
            {
                // ---- 文本增量 ----
                if (!string.IsNullOrEmpty(update.Text))
                {
                    newText.Append(update.Text);
                    sb.Append(update.Text);
                    await emit(new StreamEvent
                    {
                        Event = "delta",
                        SessionId = sessionId,
                        MessageId = messageId,
                        Delta = update.Text,
                        Content = sb.ToString(),
                    });
                }

                // ---- 工具调用 / 结果（FunctionCallContent / FunctionResultContent）----
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case FunctionCallContent fc:
                            var tool = new ToolCallInfo { Name = fc.Name, Arguments = JsonSerializer.Serialize(fc.Arguments) };
                            tools.Add(tool);
                            await emit(new StreamEvent
                            {
                                Event = "tool", SessionId = sessionId, MessageId = messageId, Tool = tool,
                            });
                            break;

                        case FunctionResultContent fr:
                            var matched = tools.FirstOrDefault(t => t.Return is null &&
                                IsResultOf(t, fr.CallId));
                            if (matched != null)
                            {
                                matched.Return = fr.Result?.ToString();
                            }
                            await emit(new StreamEvent
                            {
                                Event = "tool", SessionId = sessionId, MessageId = messageId,
                                Tool = new ToolCallInfo
                                {
                                    Name = matched?.Name ?? fr.CallId ?? "tool",
                                    Return = fr.Result?.ToString(),
                                },
                            });
                            break;

                        case ToolApprovalRequestContent approval:
                            foundApproval = approval;
                            break;
                    }
                }

                // ---- token 消耗（UsageContent 事件流携带，逐条累加）----
                foreach (var uc in update.Contents.OfType<UsageContent>())
                {
                    inputTokens += uc.Details.InputTokenCount ?? 0;
                    outputTokens += uc.Details.OutputTokenCount ?? 0;
                    totalTokens += uc.Details.TotalTokenCount ?? 0;
                    usage = new UsageInfo
                    {
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens,
                        TotalTokens = totalTokens,
                    };
                }
            }

            // ---- 未出现审批请求：正常结束 ----
            if (foundApproval is null)
            {
                break;
            }

            // ---- 出现审批请求 → standing 规则判定 ----
            var functionCall = (FunctionCallContent)foundApproval.ToolCall;
            var approvalId = functionCall.CallId;
            var toolName = functionCall.Name;

            if (_approvals.ShouldAutoApprove(sessionId, toolName))
            {
                // 常驻授权命中：自动批准并续跑（"不再询问"）
                _logger.LogInformation("工具 {Tool} 命中常驻授权，自动批准", toolName);
                input = new List<ChatMessage>
                {
                    new(ChatRole.User, new AIContent[] { foundApproval.CreateResponse(true) }),
                };
                continue;
            }

            // 需要人工决策：注册挂起审批，通知前端，结束本轮
            _pending.Set(approvalId, sessionId, toolName, foundApproval);
            pendingApproval = true;
            await emit(new StreamEvent
            {
                Event = "approval_request",
                SessionId = sessionId,
                MessageId = messageId,
                Content = sb.ToString(),
                Approval = new ApprovalRequestInfo
                {
                    ApprovalId = approvalId,
                    ToolName = toolName,
                    Arguments = JsonSerializer.Serialize(functionCall.Arguments),
                },
            });
            break;
        }

        // ---- 收尾事件 ----
        await emit(new StreamEvent
        {
            Event = pendingApproval ? "paused" : "done",
            SessionId = sessionId,
            MessageId = messageId,
            Content = sb.ToString(),
            Usage = usage,
        });
        return pendingApproval;
    }

    /// <summary>FunctionResultContent 无工具名属性：按“第一个未回填的调用”匹配</summary>
    private static bool IsResultOf(ToolCallInfo tool, string? callId) => true;
}
