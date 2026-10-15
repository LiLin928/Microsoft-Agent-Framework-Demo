using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI.LocalCodeAct;
using Microsoft.Extensions.Configuration;
using OpenAI;
using System.ClientModel;
using System.ComponentModel;

namespace MafAgent.Demo;

/// <summary>
/// Microsoft Agent Framework 能力演示（对标 SK8 迁移 + 五大新能力）：
///   1. Agent Harness     —— 长时间多步推理（todo 规划 / 模式机 / 迭代上限）
///   2. Context Compaction —— MaxContextWindowTokens 预算触发的两阶段压缩
///   3. Standing Approvals —— 人工审批 + "不再询问"常驻授权
///   4. CodeAct Sandbox   —— Python 子进程执行模型生成的代码（AST 校验 + 白名单）
///   5. Provider 矩阵     —— OpenAI 兼容 / Azure / OpenAI 官方
///
/// 运行前设置环境变量：
///   DEMO_ENDPOINT=https://api.deepseek.com/v1   （或其它 OpenAI 兼容地址）
///   DEMO_APIKEY=sk-xxx
///   DEMO_MODEL=deepseek-chat
/// </summary>
public static class Program
{
    /// <summary>
    /// 演示入口：组装 Harness Agent（多步推理+上下文压缩+常驻审批+CodeAct 沙箱）并进入交互循环。
    /// 运行前在 appsettings.json 配置 endpoint/apiKey/model（DEMO_ 前缀环境变量可覆盖）。
    /// </summary>
    public static async Task Main()
    {
        // ========== 配置：读 appsettings.json（DEMO_ 前缀环境变量可覆盖同名项）==========
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables("DEMO_")
            .Build();

        var endpoint = configuration["endpoint"] ?? "https://api.deepseek.com/v1";
        var apiKey = configuration["apiKey"] ?? "";
        var model = configuration["model"] ?? "deepseek-chat";
        var pythonPath = configuration["pythonExecutable"] ?? (OperatingSystem.IsWindows() ? "python" : "python3");

        Console.WriteLine("=== Microsoft Agent Framework (MAF) Harness Demo ===");
        Console.WriteLine($"Provider: {endpoint} | model: {model} | python: {pythonPath}");
        Console.WriteLine();

        // ===== 5. Provider 矩阵：OpenAI 兼容 endpoint → IChatClient =====
        IChatClient chatClient = new OpenAIClient(new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
            .GetChatClient(model)
            .AsIChatClient();

        // ===== 4. CodeAct 沙箱：Python 子进程执行代码 =====
        using var codeAct = new LocalCodeActProvider(pythonPath, new LocalCodeActProviderOptions
        {
            ExecutionLimits = new ProcessExecutionLimits { TimeoutSeconds = 60 },
            Tools = new AIFunction[]
            {
                AIFunctionFactory.Create(QueryExchangeRate, name: "query_exchange_rate",
                    description: "查询人民币兑美元汇率（宿主工具，沙箱内 call_tool(\"query_exchange_rate\") 调用）"),
            },
        });

        // ===== 工具：安全工具 + 敏感工具（审批演示）=====
        AIFunction safeCalc = AIFunctionFactory.Create(
            (double a, double b) => a * b,
            name: "multiply",
            description: "两个数相乘（低风险，自动批准）");

        AIFunction deleteFile = AIFunctionFactory.Create(
            DeleteFile,
            name: "delete_file",
            description: "删除指定路径的文件（高风险，需要人工审批）");
        AITool approvalRequiredDelete = new ApprovalRequiredAIFunction(deleteFile); // ★ 审批包装

        // ===== 1+2+3. Harness Agent：多步推理 + 上下文压缩 + 常驻审批 =====
        AIAgent agent = chatClient.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = "ops-agent",
            HarnessInstructions = "面对复杂任务先用 todos 拆解计划，再逐步执行；执行删除等敏感操作前必须说明原因。",
            ChatOptions = new ChatOptions
            {
                Instructions = "你是一名运维助手，擅长数据分析与文件管理，请用中文回答。",
                Tools = new AITool[] { safeCalc, approvalRequiredDelete },
            },
            MaxContextWindowTokens = 32768,   // ★ 上下文压缩预算（超限自动两阶段压缩）
            MaxOutputTokens = 8192,
            MaximumIterationsPerRequest = 40, // 多步推理循环上限
            DisableToolAutoApproval = false,  // 启用审批 Agent
            AIContextProviders = new List<AIContextProvider> { codeAct }, // ★ CodeAct 注入 execute_code
        });

        var standingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // "不再询问"记录
        AgentSession session = await agent.CreateSessionAsync();

        Console.WriteLine("输入任务开始对话（示例：");
        Console.WriteLine("  · 帮我用代码计算 1 到 100 的平方和并给出前3项     ← 触发 CodeAct");
        Console.WriteLine("  · 删除 C:\\temp\\old-report.txt                    ← 触发人工审批");
        Console.WriteLine("  · 分析1-12月销售额 [123,456,...] 并画图总结      ← 多步推理");
        Console.WriteLine("输入 exit 退出。\n");

        while (true)
        {
            Console.Write("你> ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) continue;
            if (input.Trim() is "exit" or "quit") break;

            var messages = new List<ChatMessage> { new(ChatRole.User, input) };
            var rounds = 0;

            while (true)
            {
                rounds++;
                if (rounds > 10) { Console.WriteLine("[达到自动续跑上限]"); break; }

                ToolApprovalRequestContent? approval = null;

                await foreach (var update in agent.RunStreamingAsync(messages, session))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        Console.Write(update.Text);
                    }
                    foreach (var c in update.Contents)
                    {
                        if (c is ToolApprovalRequestContent req) approval = req;
                        else if (c is FunctionCallContent fc)
                            Console.WriteLine($"\n[工具调用] {fc.Name}({System.Text.Json.JsonSerializer.Serialize(fc.Arguments)})");
                        else if (c is FunctionResultContent fr)
                            Console.WriteLine($"\n[工具返回] → {Truncate(fr.Result?.ToString(), 200)}");
                    }
                }
                Console.WriteLine();

                if (approval is null) break; // 正常结束

                // ===== 3. Standing Approvals：人工审批交互 =====
                var call = (FunctionCallContent)approval.ToolCall;
                Console.Write($"\n⚠ 需要审批：{call.Name}({System.Text.Json.JsonSerializer.Serialize(call.Arguments)})");
                if (standingTools.Contains(call.Name))
                {
                    Console.WriteLine("  [命中常驻授权，自动批准]");
                    messages = [new ChatMessage(ChatRole.User, new AIContent[] { approval.CreateResponse(true) })];
                    continue;
                }

                Console.WriteLine();
                Console.Write("  允许执行? [y=允许 / n=拒绝 / a=始终允许(不再询问)] > ");
                var key = Console.ReadLine()?.Trim().ToLowerInvariant();

                var approved = key is "y" or "a";
                if (key == "a") standingTools.Add(call.Name); // standing approval

                messages = [new ChatMessage(ChatRole.User, new AIContent[] { approval.CreateResponse(approved) })];
            }
            Console.WriteLine();
        }
    }

    /// <summary>宿主工具示例：返回固定汇率，供沙箱内 call_tool("query_exchange_rate") 调用</summary>
    /// <returns>人民币兑美元汇率</returns>
    [Description("返回固定汇率 7.2（演示宿主工具）")]
    private static double QueryExchangeRate() => 7.2;

    /// <summary>高风险工具示例：删除文件（演示模式仅打印，不真实删除），用于触发人工审批流程</summary>
    /// <param name="path">文件完整路径</param>
    /// <returns>删除结果描述</returns>
    [Description("删除文件（演示用，仅打印)")]
    private static string DeleteFile([Description("文件完整路径")] string path)
    {
        return $"[演示模式] 已删除 {path}（未真实删除）";
    }

    /// <summary>把工具返回文本截断到 max 字符（控制控制台输出长度）</summary>
    /// <param name="s">原始文本</param>
    /// <param name="max">最大保留字符数</param>
    /// <returns>截断后的文本</returns>
    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "…";
    }
}
