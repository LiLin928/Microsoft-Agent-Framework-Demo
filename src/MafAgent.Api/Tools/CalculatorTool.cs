using System.ComponentModel;
using Microsoft.Extensions.AI;
using System.Text.Json.Serialization;

namespace MafAgent.Api.Tools;

/// <summary>
/// 内置数学运算工具（迁移自 SK8 的 DefaultTool，[KernelFunction] → [Description] 参数描述，由 AIFunctionFactory 自动转成工具）。
/// SK 写法: [KernelFunction("Add")]；MAF 写法: AIFunctionFactory.Create(方法, name: "Add")。
/// </summary>
public static class CalculatorTool
{
    /// <summary>统一返回结构（兼容 SK8 ResultModel）</summary>
    public class Result
    {
        [JsonPropertyName("计算结果")]
        public object data { get; set; } = 0;
    }

    /// <summary>构造统一返回结构的辅助方法</summary>
    /// <param name="v">运算结果值</param>
    /// <returns>包装后的 Result</returns>
    private static Result Of(double v) => new() { data = v };

    /// <summary>加法：返回 num1 + num2</summary>
    /// <param name="num1">第一个数字</param>
    /// <param name="num2">第二个数字</param>
    /// <returns>计算结果</returns>
    [Description("加法运算")]
    public static Result Add([Description("第一个数字")] double num1, [Description("第二个数字")] double num2) => Of(num1 + num2);

    /// <summary>减法：返回 num1 - num2</summary>
    /// <param name="num1">被减数</param>
    /// <param name="num2">减数</param>
    /// <returns>计算结果</returns>
    [Description("减法运算")]
    public static Result Subtraction([Description("被减数")] double num1, [Description("减数")] double num2) => Of(num1 - num2);

    /// <summary>乘法：返回 num1 * num2</summary>
    /// <param name="num1">第一个数字</param>
    /// <param name="num2">第二个数字</param>
    /// <returns>计算结果</returns>
    [Description("乘法运算")]
    public static Result Multiplication([Description("第一个数字")] double num1, [Description("第二个数字")] double num2) => Of(num1 * num2);

    /// <summary>除法：返回 num1 / num2，除数为 0 时返回 NaN</summary>
    /// <param name="num1">被除数</param>
    /// <param name="num2">除数</param>
    /// <returns>计算结果</returns>
    [Description("除法运算")]
    public static Result Division([Description("被除数")] double num1, [Description("除数")] double num2) => Of(num2 == 0 ? double.NaN : num1 / num2);

    /// <summary>平方根：返回 num 的算术平方根</summary>
    /// <param name="num">非负数字</param>
    /// <returns>计算结果</returns>
    [Description("计算平方根")]
    public static Result Sqrt([Description("数字")] double num) => Of(Math.Sqrt(num));

    /// <summary>绝对值：返回 num 的绝对值</summary>
    /// <param name="num">输入数字</param>
    /// <returns>计算结果</returns>
    [Description("计算绝对值")]
    public static Result Abs([Description("数字")] double num) => Of(Math.Abs(num));

    /// <summary>最大值：返回两个数中较大者</summary>
    /// <param name="num1">第一个数字</param>
    /// <param name="num2">第二个数字</param>
    /// <returns>计算结果</returns>
    [Description("计算最大值")]
    public static Result Max([Description("第一个数字")] double num1, [Description("第二个数字")] double num2) => Of(Math.Max(num1, num2));

    /// <summary>最小值：返回两个数中较小者</summary>
    /// <param name="num1">第一个数字</param>
    /// <param name="num2">第二个数字</param>
    /// <returns>计算结果</returns>
    [Description("计算最小值")]
    public static Result Min([Description("第一个数字")] double num1, [Description("第二个数字")] double num2) => Of(Math.Min(num1, num2));

    /// <summary>余弦：返回弧度 num 的余弦值</summary>
    /// <param name="num">弧度值</param>
    /// <returns>计算结果</returns>
    [Description("计算余弦值")]
    public static Result Cos([Description("弧度")] double num) => Of(Math.Cos(num));

    /// <summary>批量注册全部运算工具</summary>
    public static IEnumerable<AIFunction> CreateAll() => new AIFunction[]
    {
        AIFunctionFactory.Create(Add, name: "Add"),
        AIFunctionFactory.Create(Subtraction, name: "Subtraction"),
        AIFunctionFactory.Create(Multiplication, name: "Multiplication"),
        AIFunctionFactory.Create(Division, name: "Division"),
        AIFunctionFactory.Create(Sqrt, name: "Sqrt"),
        AIFunctionFactory.Create(Abs, name: "Abs"),
        AIFunctionFactory.Create(Max, name: "Max"),
        AIFunctionFactory.Create(Min, name: "Min"),
        AIFunctionFactory.Create(Cos, name: "Cos"),
    };
}
