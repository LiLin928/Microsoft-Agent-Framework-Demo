using MafAgent.Api.Agents;
using MafAgent.Api.Approvals;
using MafAgent.Api.DataAccess;
using MafAgent.Api.Providers;
using Yitter.IdGenerator;

var builder = WebApplication.CreateBuilder(args);

// ========== 业务服务 ==========
builder.Services.AddSingleton<LlmProviderMatrix>();          // Provider 矩阵
builder.Services.AddSingleton<StandingApprovalStore>();      // 常驻工具审批存储
builder.Services.AddSingleton<AgentSessionRegistry>();       // 会话注册表
builder.Services.AddSingleton<PendingApprovalRegistry>();    // 挂起审批注册表
builder.Services.AddSingleton<AgentHost>();                  // Agent 装配
builder.Services.AddSingleton<AgentRunner>();                // 流式运行器

// ========== 知识库配置（迁移自 SK8 BaseFilterExtensions/AppsettingsInit）==========
var modelOptions = builder.Configuration.GetSection("modelConfig").GetChildren()
    .ToDictionary(c => c.Key, c => c.GetChildren().ToDictionary(x => x.Key, x => x.Value ?? ""));
DatabaseConfig.configuration = builder.Configuration;
DatabaseConfig.embeddingConfig = new ModelOptions
{
    endpoint = modelOptions.GetValueOrDefault("embedding", new Dictionary<string, string?>()).GetValueOrDefault("endpoint"),
    apikey = modelOptions.GetValueOrDefault("embedding", new Dictionary<string, string?>()).GetValueOrDefault("apikey"),
    model = modelOptions.GetValueOrDefault("embedding", new Dictionary<string, string?>()).GetValueOrDefault("model"),
};
DatabaseConfig.rerankConfig = new ModelOptions
{
    endpoint = modelOptions.GetValueOrDefault("rerank", new Dictionary<string, string?>()).GetValueOrDefault("endpoint"),
    apikey = modelOptions.GetValueOrDefault("rerank", new Dictionary<string, string?>()).GetValueOrDefault("apikey"),
    model = modelOptions.GetValueOrDefault("rerank", new Dictionary<string, string?>()).GetValueOrDefault("model"),
};

// ========== 雪花 ID 生成器（迁移自 SK8 Program.cs）==========
// VectorTool 建库/插入数据用 YitIdHelper.NextId() 生成主键，必须先初始化，
// 否则内部生成器实例为 null，调用 NextId() 会抛 NullReferenceException；
// workerId 在多实例部署时每实例必须唯一（1~63）
YitIdHelper.SetIdGenerator(new IdGeneratorOptions(1));

// ========== Swagger ==========
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddControllers();

var app = builder.Build();

// 会话过期清理（后台每 5 分钟）
var sessionRegistry = app.Services.GetRequiredService<AgentSessionRegistry>();
var cleanupTimer = new PeriodicTimer(TimeSpan.FromMinutes(5));
_ = Task.Run(async () =>
{
    while (await cleanupTimer.WaitForNextTickAsync())
    {
        try { sessionRegistry.Cleanup(); } catch { }
    }
});

app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();

app.Run();
