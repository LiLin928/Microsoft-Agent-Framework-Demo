using Npgsql;
using SqlSugar;

namespace MafAgent.Api.DataAccess
{
    public class SqlSugarForPgsqlASVector
    {
        /// <summary>SqlSugar 客户端实例（连接已切换为启用 pgvector 的 Npgsql 连接）</summary>
        public SqlSugarClient sqlSugarClient { get; set; }


                /// <summary>
        /// 构造函数：初始化 SqlSugar 客户端并接入 PostgreSQL + pgvector。
        /// 配置要点：PostgreSQL 方言、自动关闭连接、可空属性 AOP 映射、禁用删列；
        /// 随后通过 NpgsqlDataSourceBuilder.UseVector() 构建向量数据源并打开物理连接，
        /// 挂到 SqlSugar 上使 SQL 可直接读写 vector 列。
        /// </summary>
        /// <param name="ConnectionString">PostgreSQL 连接字符串</param>
public SqlSugarForPgsqlASVector(string ConnectionString)
        {
            ConnectionConfig config = new ConnectionConfig();
            var ConfigureExternalService = new ConfigureExternalServices();

            config.DbType = DbType.PostgreSQL;
            config.ConnectionString = ConnectionString;
            config.IsAutoCloseConnection = true;

            config.MoreSettings = new ConnMoreSettings()
            {
                PgSqlIsAutoToLower = false,//增删查改支持驼峰表
                PgSqlIsAutoToLowerCodeFirst = false, // 建表建驼峰表。5.1.3.30 
            };

            //注意:  这儿AOP设置不能少
            ConfigureExternalService.EntityService = (c, p) =>
            {
                // int?  decimal?这种 isnullable=true
                if (c.PropertyType.IsGenericType &&
                c.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    p.IsNullable = true;

                }
            };
            ConfigureExternalService.EntityNameService = (type, entity) =>
            {
                entity.IsDisabledDelete = true; //禁止删除列
                                                // entity.IsDisabledUpdateAll= true;//禁止更新列
            };
            config.ConfigureExternalServices = ConfigureExternalService;
            sqlSugarClient = new SqlSugarClient(config);

            var dataSourceBuilder = new NpgsqlDataSourceBuilder(sqlSugarClient.CurrentConnectionConfig.ConnectionString);

            dataSourceBuilder.UseVector();

            using var dataSource = dataSourceBuilder.Build();

            //sqlSugarClient.DbMaintenance.CreateDatabase();

            var conn = dataSource.OpenConnection();

            sqlSugarClient.Ado.Connection = conn;
        }

    }
}
