using System.ComponentModel;
using Microsoft.Extensions.AI;
using MafAgent.Api.Agents;
using MafAgent.Api.DataAccess;

namespace MafAgent.Api.Tools;

/// <summary>
/// 知识库检索工具：把 pgvector 知识库暴露为 Agent 工具（SK8 中检索是"请求前注入"，
/// MAF 里既可以保留注入方式，也可以让 Agent 在推理过程中自主调用本工具）。
/// </summary>
public static class KnowledgeTool
{
    /// <summary>
    /// 在指定 pgvector 知识库中做语义检索，返回相关文档片段（标题+内容，多个用 --- 分隔）。
    /// 供 Agent 在推理过程中自主调用，替代/补充 SK8 的“请求前注入”方式。
    /// </summary>
    /// <param name="dbid">知识库 id</param>
    /// <param name="query">检索问题或关键词</param>
    /// <param name="top">返回条数，默认 2</param>
    /// <returns>检索结果文本，未命中时返回提示语</returns>
    [Description("在指定知识库中语义检索相关文档片段，返回标题与内容。适用于需要引用内部资料回答问题的场景。")]
    public static async Task<string> SearchKnowledge(
        [Description("知识库id(dbid)")] string dbid,
        [Description("检索问题或关键词")] string query,
        [Description("返回条数，默认2")] int top = 2)
    {
        var ret = VectorTool.selectVector(dbid, query, top);
        if (!ret.issuccess || ret.data.Count == 0)
        {
            return "未检索到相关内容。";
        }

        return string.Join("\n---\n", ret.data.Select(d => $"【{d.title}】\n{d.content}"));
    }

    /// <summary>注册为 AIFunction</summary>
    public static AIFunction Create() => AIFunctionFactory.Create(
        SearchKnowledge,
        name: "search_knowledge",
        description: "知识库语义检索工具：根据问题在指定知识库中检索最相关的文档片段。");
}
