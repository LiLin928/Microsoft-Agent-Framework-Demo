using ModelContextProtocol.Server;
using System.ComponentModel;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// 可选：在 SseMiddleware 中实现鉴权后取消注释
// app.UseMiddleware<SseMiddleware>();

app.MapMcp("/mcp"); // http://localhost:5000/mcp  (SSE / StreamableHttp)

app.Run();

/// <summary>
/// 计算工具（迁移自 SK8 McpService CalculateTool）
/// </summary>
[McpServerToolType]
public static class CalculateTool
{
    /// <summary>计算两个数的和并返回结果文本</summary>
    /// <param name="number1">第一个数字</param>
    /// <param name="number2">第二个数字</param>
    /// <returns>形如 "计算结果: 3" 的结果文本</returns>
    [McpServerTool, Description("计算俩个数的和")]
    public static async Task<string> Add(
        [Description("第一个数字")] int number1,
        [Description("第二个数字")] int number2)
    {
        await Task.CompletedTask;
        return $"计算结果: {number1 + number2}";
    }

    /// <summary>按姓名查询演示用的个人信息</summary>
    /// <param name="name">姓名</param>
    /// <returns>个人信息描述文本</returns>
    [McpServerTool, Description("查询个人信息")]
    public static async Task<string> SelectUserInfo(
        [Description("姓名")] string name)
    {
        await Task.CompletedTask;
        return $"{name},非洲人，出生于1988年11月11日，工作年限10年，目前在阿三公司工作。";
    }
}

/// <summary>回声工具（迁移自 SK8）</summary>
[McpServerToolType]
public static class EchoTool
{
    /// <summary>回声工具：把消息原样回显给客户端</summary>
    /// <param name="message">待回显的消息</param>
    /// <returns>带前缀的回显文本</returns>
    [McpServerTool, Description("Echoes the message back to the client.")]
    public static string Echo(string message) => $"hello {message}";
}
