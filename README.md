# MAF —— Microsoft Agent Framework 版 AI 中间件

参考 `D:\4-MyProject\SemanticKernelFornet8`（SemanticKernel 1.52 + net8）用 **Microsoft Agent Framework (MAF)** 重新实现，
并在原有能力（知识库检索 / 内置插件 / OpenAPI 转插件 / MCP）之上新增五大能力：

| 能力 | 说明 |
|---|---|
| **Agent Harness** | 长时间、多步推理 Agent：todo 规划、plan/execute 模式机、会话记忆、迭代上限，官方"开箱即用"智能体 |
| **上下文压缩 (Context Compaction)** | 长对话不爆 token：两阶段压缩（先折叠旧工具结果、再截断最旧消息组） |
| **常驻工具审批 (Standing Tool Approvals)** | 工具调用人工确认 + "不再询问"常驻授权（会话级 / 全局级 / 内置安全名单 / 请求级规则） |
| **CodeAct Sandbox** | Python 子进程执行模型生成的代码：AST 校验 + 导入白名单 + 宿主工具注入 + 超时限制 |
| **Provider 矩阵** | OpenAI 兼容（DeepSeek / SiliconFlow / Kimi / 通义 / Ollama / vLLM…）、Azure OpenAI、OpenAI 官方，配置即切换 |

## 版本与项目

- 目标框架：`net10.0`（本机 SDK 10.0.401；MAF 1.25.0 支持 net8/9/10）
- MAF：`Microsoft.Agents.AI 1.25.0`、`Microsoft.Agents.AI.Harness 1.25.0`、`Microsoft.Agents.AI.LocalCodeAct 1.25.0-preview.261014.1`
- MCP C# SDK：`2.2.0`（Core / AspNetCore 拆分包）
- MEAI：`Microsoft.Extensions.AI(.OpenAI) 10.11.0` + `OpenAI 2.14.0` + `Azure.AI.OpenAI 2.1.0`

```
D:\4-MyProject\MAF\
├── MAF.slnx
├── Directory.Packages.props        # NuGet 版本集中管理
├── global.json                     # SDK 版本锁定
└── src\
    ├── MafAgent.Api\               # Web API 中间件（对标 semantic-kernel-api）
    │   ├── Providers\LlmProviderMatrix.cs   # ★ Provider 矩阵
    │   ├── Agents\AgentHost.cs              # ★ Agent 装配（Harness/轻量 + 压缩 + CodeAct + 审批规则）
    │   ├── Agents\AgentRunner.cs            # ★ 流式运行 + 审批协调 + 会话注册
    │   ├── Approvals\StandingApprovalStore.cs # ★ 常驻审批存储
    │   ├── Tools\CalculatorTool.cs          # 对标 DefaultTool（数学运算）
    │   ├── Tools\KnowledgeTool.cs           # 知识库检索 → Agent 工具
    │   ├── Tools\OpenApiToolFactory.cs      # 对标 OpenApiKernelPluginFactory
    │   ├── Mcp\McpToolLoader.cs             # 对标 MCP 客户端（Stdio/SSE/Http）
    │   ├── DataAccess\                      # 迁移自 SK8（pgvector 知识库，原样保留）
    │   └── Controllers\ChatController.cs    # SSE 流式 + 审批决策接口
    ├── MafAgent.Demo\              # 控制台交互演示（五大能力一键体验）
    ├── McpService\                 # MCP SSE/Http 服务端（对标）
    └── McpStdio\                   # MCP Stdio 服务端（对标，环境变量鉴权）
```

## SK8 → MAF 迁移对照

| SemanticKernel (SK8) | MAF (本项目) |
|---|---|
| `Kernel.CreateBuilder().AddOpenAIChatCompletion(model, uri, key)` | `OpenAIClient(ApiKeyCredential, OpenAIClientOptions{Endpoint}).GetChatClient(model).AsIChatClient()` → `LlmProviderMatrix` |
| `IChatCompletionService.GetStreamingChatMessageContentsAsync` | `agent.RunStreamingAsync(messages, session)` |
| `ChatHistory` + 前端回传 history | `AgentSession`（框架原生多轮状态，前端只传 sessionId）+ 兼容保留 history 入参 |
| `ChatHistoryTruncationReducer(targetCount)` | `CompactionProvider(new ContextWindowCompactionStrategy(...))` / Harness `MaxContextWindowTokens` |
| `[KernelFunction]` + `AddFromType<DefaultTool>` | `AIFunctionFactory.Create(方法, name:)` |
| `OpenApiKernelPluginFactory.CreateFromOpenApiAsync` | 自实现 `OpenApiToolFactory.CreateFromOpenApiAsync`（swagger.json → AIFunction） |
| `McpClientFactory` + `ToKernelFunction` 手工转换 | `McpClient.CreateAsync` + `ListToolsAsync()`（McpClientTool 本身就是 AIFunction，零转换） |
| `AddOpenAIChatCompletion` 任意厂商 | Provider 矩阵（kind: openai-compatible / azure / openai） |
| `McpService` / `Mcp_Stdio`（0.1-preview） | 同名项目升级到 MCP SDK 2.2.0（注意：2.x 无 `IMcpServer` 参数，服务端拆分为 Core/AspNetCore 包） |

## 快速开始

```bash
# 1) 配置 appsettings.json 中的 LlmProviders（填真实 apiKey）与 VectorDb（可选）
# 2) 启动主 API
dotnet run --project src/MafAgent.Api --urls http://localhost:5234
# 3)（可选）启动 MCP 服务端
dotnet run --project src/McpService --urls http://localhost:5257
# 4) 控制台体验五大能力（需设置 DEMO_APIKEY）
DEMO_ENDPOINT=https://api.deepseek.com/v1 DEMO_APIKEY=sk-xxx dotnet run --project src/MafAgent.Demo
```

## 接口示例

### 1. SSE 流式对话（轻量模式，对标 SK8）

POST `http://localhost:5234/api/Chat/SendMessage`

```json
{
  "providerName": "deepseek",
  "Query": "9.11 和 9.9 谁大？请用工具计算差值",
  "prop": "你是一名热于助人的ai助手",
  "mcpServers": {
    "tool":   { "command": "dotnet", "args": ["D:\\4-MyProject\\MAF\\src\\McpStdio\\bin\\Debug\\net10.0\\McpStdio.dll"], "env": { "key": "123456" } },
    "ssetool":{ "url": "http://localhost:5257/mcp", "transportMode": "streamablehttp" }
  },
  "apilist": [ { "uri": "http://localhost:5234/swagger/v1/swagger.json", "name": "test" } ]
}
```

也可完全兼容 SK8 传法（动态三件套）：`endpoint` + `apiKey` + `modelId`。

SSE 事件：`delta`（文本增量）→ `tool`（工具调用/返回）→ `done`（含 usage）；需要人工审批时发 `approval_request`。

### 2. Harness 长任务模式（多步推理 + CodeAct）

```json
{
  "providerName": "deepseek",
  "Query": "先用代码统计 1~100 的平方和，再列出执行计划",
  "useHarness": true,
  "enableCodeAct": true,
  "maximumIterationsPerRequest": 40,
  "maxContextWindowTokens": 32768
}
```

Harness 自动启用：todo 拆解、plan/execute 模式、会话文件记忆、上下文压缩、工具审批、OTel 可观测。

### 3. 工具审批：人工决策 + "不再询问"

当模型要调用高风险工具（被 `ApprovalRequiredAIFunction` 包装，或 Harness 判定需要确认）时，
SSE 返回：

```json
{"event":"approval_request","sessionId":"xxx","approval":{"approvalId":"call_abc","toolName":"delete_file","arguments":"{\"path\":\"D:\\\\a.txt\"}"}}
```

POST `http://localhost:5234/api/Chat/ResolveApproval`（SSE 续跑）：

```json
{ "sessionId": "xxx", "approvalId": "call_abc", "approved": true, "remember": true }
```

`remember=true` → 该工具进入全局常驻授权（standing approval），后续调用自动放行。
会话级常驻授权 / 内置安全名单 / 请求级 `autoApproveTools` 见 `StandingApprovalStore`。

### 4. 知识库（迁移自 SK8，接口不变）

```
POST /api/Knowledge/CreateVectorDb?title=测试
POST /api/System/Install
POST /api/Knowledge/InsertVector?id=&dbid=xxx&title=文档&content=...
POST /api/Knowledge/selectVector?dbid=xxx&content=问题&top=2
```

对话时传 `dbid` 自动检索注入；Harness 模式下 Agent 还可自主调用 `search_knowledge` 工具。

### 5. Provider 矩阵

```
GET /api/Chat/Providers
```

## 五大能力实现位置（速查）

| 能力 | 代码位置 | 关键 API |
|---|---|---|
| Agent Harness | `AgentHost.BuildAsync`（useHarness=true 分支） | `chatClient.AsHarnessAgent(new HarnessAgentOptions{...})` |
| 上下文压缩 | 同上（MaxContextWindowTokens / CompactionProvider） | `ContextWindowCompactionStrategy(maxCtx, maxOut, 0.6, 0.8)`（60% 预算折叠工具结果、80% 截断） |
| 常驻工具审批 | `StandingApprovalStore` + `AgentRunner.StreamRunAsync` + Harness `ToolApprovalAgentOptions.AutoApprovalRules` | `ApprovalRequiredAIFunction` / `ToolApprovalRequestContent.CreateResponse(bool)` |
| CodeAct 沙箱 | `AgentHost.CreateCodeActProvider`；Demo 中直接演示 | `new LocalCodeActProvider(pythonPath, LocalCodeActProviderOptions{...})` |
| Provider 矩阵 | `LlmProviderMatrix` | `AsIChatClient()`（OpenAI 兼容/Azure/官方） |

## 注意事项

1. **Harness / LocalCodeAct 为实验性 API**（诊断码 `MAAI001`，已在 csproj 中 NoWarn），生产使用请锁定 1.25.0 版本。
2. **CodeAct 需要本机 Python 3.10+**（`AgentDefaults:pythonExecutable` 可指定解释器路径），沙箱默认阻止危险导入（os.system/subprocess 等），可用 `AllowedImports`/`FileMounts`/`Environment` 精细控制。
3. **知识库需要 PostgreSQL + pgvector 扩展**（1024 维 embedding，默认 SiliconFlow BAAI/bge-m3），与 SK8 完全一致。
4. MCP Stdio 示例进程需要先 `dotnet build` 生成 McpStdio.dll 再在 mcpServers 中引用。
5. 本地无真实 key 时接口会返回上游 401 错误事件 —— 属预期行为，代表 Agent 构建/流式/错误链路全部打通。
