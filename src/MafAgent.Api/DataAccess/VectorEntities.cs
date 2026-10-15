using Pgvector;

using SqlSugar;

namespace MafAgent.Api.DataAccess
{

    [SugarTable("VectorList", TableDescription = "向量库列表")]
    public class VectorListEntity
    {
        /// <summary>
        /// 主键
        /// </summary>
        [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
        public string id { get; set; }


        /// <summary>
        /// 向量库名称
        /// </summary>
        public string title { get; set; }

    }

    [SugarTable("Vector", TableDescription = "向量库")]
    public class VectorEntity
    {
        /// <summary>
        /// 主键
        /// </summary>
        [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
        public string id { get; set; }

        /// <summary>
        /// 所属向量库
        /// </summary>
        public string parentid { get; set; }

        /// <summary>
        /// 标题
        /// </summary>

        public string title { get; set; }


        /// <summary>
        /// 内容
        /// </summary>

        public string content { get; set; }

        public string groupid { get; set; }

        [SugarColumn(IsIgnore = true)]
        public double Similarity { get; set; }

        /// <summary>
        /// 向量
        /// </summary>
        [SugarColumn(SqlParameterDbType = typeof(VectorConverter))]
        public Vector? embedding { get; set; }
 


    }
}
