using SqlSugar;

namespace MafAgent.Api.DataAccess
{
    public class VectorFactory
    {

        /// <summary>
        /// 连接基础库
        /// </summary>
        /// <returns></returns>
        public static SqlSugarClient BaseVector()
        {
            var db = new SqlSugarForPgsqlASVector(DatabaseConfig.configuration["VectorDb"].ToString()).sqlSugarClient;
            return db;
        }
        /// <summary>
        /// 连接基础库
        /// </summary>
        /// <returns></returns>
        public static SqlSugarClient BaseVector(string connectionString)
        {
            var db = new SqlSugarForPgsqlASVector(connectionString).sqlSugarClient;
            return db;
        }


        



    }
}
