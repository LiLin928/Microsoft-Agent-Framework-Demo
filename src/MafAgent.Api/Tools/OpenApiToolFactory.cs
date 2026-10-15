using System.ComponentModel;
using System.Dynamic;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MafAgent.Api.Tools;

/// <summary>
/// 把 Swagger/OpenAPI 文档动态转换为 Agent 工具（对标 SK8 的 OpenApiKernelPluginFactory）。
/// SK8: OpenApiKernelPluginFactory.CreateFromOpenApiAsync(name, uri|path)
/// MAF: OpenApiToolFactory.CreateFromOpenApiAsync(name, uri|path) → IEnumerable&lt;AIFunction&gt;
/// 实现方式：解析 swagger.json 的每个 operation，生成一个 AIFunction，
/// 调用时把 LLM 给出的参数 JSON 组装为 HTTP 请求（query + body）。
/// </summary>
public static class OpenApiToolFactory
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>从 swagger.json 地址或文件路径导入全部接口为工具</summary>
    public static async Task<List<AIFunction>> CreateFromOpenApiAsync(string pluginName, string? uri, string? path)
    {
        string json;
        if (!string.IsNullOrWhiteSpace(uri))
        {
            json = await Http.GetStringAsync(uri);
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            json = await File.ReadAllTextAsync(path);
        }
        else
        {
            throw new ArgumentException("uri 与 path 至少提供一个。");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var baseUrl = ResolveBaseUrl(root, uri);
        var tools = new List<AIFunction>();

        if (!root.TryGetProperty("paths", out var paths))
        {
            return tools;
        }

        foreach (var pathItem in paths.EnumerateObject())
        {
            var route = pathItem.Name;
            foreach (var op in pathItem.Value.EnumerateObject())
            {
                var method = op.Name.ToUpperInvariant();
                if (method is not ("GET" or "POST" or "PUT" or "DELETE" or "PATCH"))
                {
                    continue; // 非接口字段（parameters/summary 等）
                }

                var operation = op.Value;
                var operationId = operation.TryGetProperty("operationId", out var oid)
                    ? oid.GetString()
                    : null;
                var toolName = SanitizeName($"{pluginName}_{operationId ?? $"{method}_{route}"}");
                var summary = operation.TryGetProperty("summary", out var sm) ? sm.GetString() : null;
                var description = operation.TryGetProperty("description", out var de) ? de.GetString() : null;

                var (paramSchemaText, queryParams, hasBody) = DescribeParameters(operation);

                var sb = new StringBuilder();
                sb.AppendLine($"{method} {route}");
                if (!string.IsNullOrWhiteSpace(summary)) sb.AppendLine(summary);
                if (!string.IsNullOrWhiteSpace(description)) sb.AppendLine(description);
                sb.AppendLine($"参数( JSON 对象 ): {paramSchemaText}");

                var desc = sb.ToString();
                var capturedRoute = route;
                var capturedMethod = method;
                var capturedQuery = queryParams;

                async Task<string> InvokeAsync(string? jsonArgs, CancellationToken ct)
                {
                    return await CallEndpointAsync(baseUrl, capturedRoute, capturedMethod, capturedQuery,
                        hasBody, jsonArgs, ct);
                }

                tools.Add(AIFunctionFactory.Create(
                    InvokeAsync,
                    name: toolName,
                    description: desc));
            }
        }

        return tools;
    }

    /// <summary>
    /// 从 swagger 文档解析服务根地址：OpenAPI 3.x 取 servers[0].url；
    /// Swagger 2.x 由 scheme+host+basePath 拼接；均缺失时退回文档自身的站点地址。
    /// </summary>
    /// <param name="root">swagger.json 根节点</param>
    /// <param name="uri">swagger 文档的来源地址（用于兜底与相对 url 解析）</param>
    /// <returns>接口根地址（不含路由）</returns>
    private static string ResolveBaseUrl(JsonElement root, string? uri)
    {
        // openapi 3.x: servers[0].url；swagger 2.x: host + basePath
        if (root.TryGetProperty("servers", out var servers) && servers.GetArrayLength() > 0)
        {
            var u = servers[0].GetProperty("url").GetString();
            if (!string.IsNullOrWhiteSpace(u))
            {
                return u.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? u : Combine(new Uri(uri!), u);
            }
        }
        if (root.TryGetProperty("host", out var host))
        {
            var scheme = root.TryGetProperty("schemes", out var sch) && sch.GetArrayLength() > 0
                ? sch[0].GetString() : "https";
            var basePath = root.TryGetProperty("basePath", out var bp) ? bp.GetString() : "";
            return $"{scheme}://{host.GetString()}{basePath}";
        }
        // 兜底：用 swagger 文档自身的地址
        var docUri = new Uri(uri!);
        return $"{docUri.Scheme}://{docUri.Authority}";
    }

    /// <summary>合并基地址与相对路径为绝对 URL</summary>
    /// <param name="baseUri">基地址</param>
    /// <param name="relative">相对路径</param>
    /// <returns>绝对 URL 字符串</returns>
    private static string Combine(Uri baseUri, string relative)
    {
        var b = new Uri(baseUri, relative);
        return b.ToString();
    }

    /// <summary>
    /// 解析 operation 的 parameters / requestBody，生成给 LLM 看的参数说明文本，
    /// 并收集 query 参数名与是否必填、是否含请求体。
    /// </summary>
    /// <param name="operation">swagger 的 operation 节点</param>
    /// <returns>参数说明文本、query 参数列表（名称+是否必填）、是否含请求体</returns>
    private static (string schemaText, List<(string name, bool required)> queryParams, bool hasBody)
        DescribeParameters(JsonElement operation)
    {
        var sb = new StringBuilder();
        var queryParams = new List<(string, bool)>();
        var hasBody = false;

        if (operation.TryGetProperty("parameters", out var parameters))
        {
            foreach (var p in parameters.EnumerateArray())
            {
                var pname = p.TryGetProperty("name", out var pn) ? pn.GetString() : "?";
                var pin = p.TryGetProperty("in", out var pi) ? pi.GetString() : "query";
                var preq = p.TryGetProperty("required", out var pr) && pr.GetBoolean();
                var ptype = p.TryGetProperty("schema", out var ps) && ps.TryGetProperty("type", out var pt)
                    ? pt.GetString() : "string";
                var pdesc = p.TryGetProperty("description", out var pd) ? pd.GetString() : "";
                sb.Append($"{pname}({pin}:{ptype}{(preq ? ",必填" : "")},{pdesc}) ");
                if (pin == "query")
                {
                    queryParams.Add((pname!, preq));
                }
            }
        }

        if (operation.TryGetProperty("requestBody", out var body) ||
            operation.TryGetProperty("consumes", out _))
        {
            hasBody = true;
            sb.Append("requestBody( application/json,请求体JSON )");
        }

        var text = sb.Length == 0 ? "无参数" : sb.ToString();
        return (text, queryParams, hasBody);
    }

    /// <summary>
    /// 真正调用接口：用 LLM 给出的参数 JSON 填充路由占位符与 query 参数，
    /// 其余字段组装为 JSON 请求体（POST/PUT/PATCH），发送 HTTP 请求并返回状态码+响应文本。
    /// </summary>
    /// <param name="baseUrl">服务根地址</param>
    /// <param name="route">接口路由（可含 {占位符}）</param>
    /// <param name="method">HTTP 方法（GET/POST/PUT/DELETE/PATCH）</param>
    /// <param name="queryParams">query 参数列表（名称+是否必填）</param>
    /// <param name="hasBody">是否携带 JSON 请求体</param>
    /// <param name="jsonArgs">LLM 生成的参数 JSON 对象字符串</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>形如 "HTTP 200\n{响应体}" 的文本（超长截断）</returns>
    private static async Task<string> CallEndpointAsync(
        string baseUrl, string route, string method, List<(string name, bool required)> queryParams,
        bool hasBody, string? jsonArgs, CancellationToken ct)
    {
        var args = string.IsNullOrWhiteSpace(jsonArgs)
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonArgs!)
              ?? new Dictionary<string, JsonElement>();

        // query 参数替换 {path} 占位符
        foreach (var placeholder in ExtractPlaceholders(route))
        {
            if (args.TryGetValue(placeholder, out var v))
            {
                route = route.Replace($"{{{placeholder}}}", v.ToString());
            }
        }

        var query = new List<string>();
        foreach (var (name, _) in queryParams)
        {
            if (args.TryGetValue(name, out var qv))
            {
                query.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(qv.ToString())}");
            }
        }

        var url = $"{baseUrl.TrimEnd('/')}/{route.TrimStart('/')}";
        if (query.Count > 0)
        {
            url += "?" + string.Join("&", query);
        }

        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (hasBody && (method is "POST" or "PUT" or "PATCH"))
        {
            // 其余参数全部作为 body
            var bodyArgs = args
                .Where(kv => !queryParams.Contains((kv.Key, true)) && !queryParams.Contains((kv.Key, false)))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var bodyJson = JsonSerializer.Serialize(bodyArgs);
            req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        }

        using var resp = await Http.SendAsync(req, ct);
        var content = await resp.Content.ReadAsStringAsync(ct);
        return $"HTTP {(int)resp.StatusCode}\n{Truncate(content, 8000)}";
    }

    /// <summary>提取路由中的 {占位符} 名称列表，如 /users/{id} 返回 ["id"]</summary>
    /// <param name="route">接口路由</param>
    /// <returns>占位符名称列表</returns>
    private static List<string> ExtractPlaceholders(string route)
    {
        var list = new List<string>();
        var start = -1;
        for (var i = 0; i < route.Length; i++)
        {
            if (route[i] == '{') start = i + 1;
            else if (route[i] == '}' && start >= 0)
            {
                list.Add(route[start..i]);
                start = -1;
            }
        }
        return list;
    }

    /// <summary>把插件名+operationId 清洗为合法工具名：非法字符替换为下划线，超长截断到 64 字符</summary>
    /// <param name="name">原始名称</param>
    /// <returns>合法工具名</returns>
    private static string SanitizeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }
        var s = sb.ToString();
        return s.Length > 64 ? s[..64] : s;
    }

    /// <summary>把响应文本截断到 max 字符，超出部分以省略提示结尾</summary>
    /// <param name="s">原始文本</param>
    /// <param name="max">最大保留字符数</param>
    /// <returns>截断后的文本</returns>
    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"\n...(截断，总长{s.Length})";
}
