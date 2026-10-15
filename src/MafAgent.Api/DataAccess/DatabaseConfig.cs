using Microsoft.Extensions.Configuration;

namespace MafAgent.Api.DataAccess;

/// <summary>数据库与向量化模型配置（迁移自 SK8 的 DatabaseCongif，修正拼写）</summary>
public static class DatabaseConfig
{
    private static IConfiguration? _configuration;

    public static IConfiguration configuration
    {
        get => _configuration ?? throw new InvalidOperationException("DatabaseConfig 未初始化");
        set => _configuration = value;
    }

    public static ModelOptions embeddingConfig { get; set; } = new();

    public static ModelOptions rerankConfig { get; set; } = new();
}

/// <summary>向量化 / Rerank 模型配置</summary>
public class ModelOptions
{
    public string? endpoint { get; set; }
    public string? apikey { get; set; }
    public string? model { get; set; }
}
