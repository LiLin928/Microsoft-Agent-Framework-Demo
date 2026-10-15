using System.Collections.Concurrent;

namespace MafAgent.Api.Approvals;

/// <summary>
/// 常驻工具审批（Standing Tool Approvals）存储。
/// 三层决策：
/// 1. 内置安全名单：纯只读/计算类工具永远自动批准（不打扰用户）；
/// 2. 会话内授权：用户勾选"本会话不再询问"后，同会话同工具自动批准；
/// 3. 全局授权：用户勾选"始终允许"后，跨会话自动批准。
/// 未命中任何层的工具调用 → 挂起等待人工决策（前端收到 approval_request 事件）。
/// </summary>
public class StandingApprovalStore
{
    /// <summary>内置自动批准名单：低风险只读工具</summary>
    private static readonly HashSet<string> BuiltInSafeTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Add", "Subtraction", "Multiplication", "Division", "Sqrt", "Abs", "Max", "Min", "Cos",
        "search_knowledge", "Echo", "Add_", "echo",
    };

    /// <summary>全局常驻授权：toolName → true</summary>
    private readonly ConcurrentDictionary<string, byte> _globalApproved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>会话级授权：sessionId → { toolName }</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sessionApproved = new();

    /// <summary>请求内指定（autoApproveTools）缓存：sessionId → { toolName }</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _requestApproved = new();

    /// <summary>注册请求内指定的自动批准工具</summary>
    public void RegisterRequestAutoApprove(string sessionId, IEnumerable<string>? toolNames)
    {
        if (toolNames is null) return;
        var set = _requestApproved.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        foreach (var t in toolNames)
        {
            set[t] = 1;
        }
    }

    /// <summary>是否自动批准（standing 规则链）</summary>
    public bool ShouldAutoApprove(string sessionId, string toolName)
    {
        if (BuiltInSafeTools.Contains(toolName)) return true;
        if (_globalApproved.ContainsKey(toolName)) return true;
        if (_sessionApproved.TryGetValue(sessionId, out var s) && s.ContainsKey(toolName)) return true;
        if (_requestApproved.TryGetValue(sessionId, out var r) && r.ContainsKey(toolName)) return true;
        return false;
    }

    /// <summary>记住授权（remember=true 时调用）</summary>
    public void Remember(string sessionId, string toolName, bool global)
    {
        if (global)
        {
            _globalApproved[toolName] = 1;
        }
        else
        {
            var set = _sessionApproved.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            set[toolName] = 1;
        }
    }

    /// <summary>撤销某工具的全局授权</summary>
    public bool RevokeGlobal(string toolName) => _globalApproved.TryRemove(toolName, out _);
}
