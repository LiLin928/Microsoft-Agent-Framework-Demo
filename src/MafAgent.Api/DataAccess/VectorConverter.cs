using SqlSugar;
using System.Data;

namespace MafAgent.Api.DataAccess
{
    public class VectorConverter : ISugarDataConverter
    {
        /// <summary>
        /// 写入转换：把 Pgvector.Vector 列值包装为 SqlSugar 参数（DbType.Object）。
        /// 实现 ISugarDataConverter 接口，向量列插入/更新时由 SqlSugar 回调。
        /// </summary>
        /// <typeparam name="T">列属性类型（应为 Pgvector.Vector）</typeparam>
        /// <param name="columnValue">列原始值，非 Pgvector.Vector 时抛出 ArgumentException</param>
        /// <param name="columnIndex">参数序号，用于生成唯一参数名</param>
        /// <returns>可被 Npgsql 识别为 vector 类型的 SqlSugar 参数</returns>
        public SugarParameter ParameterConverter<T>(object columnValue, int columnIndex)
        {

            // null check
            if (columnValue is not Pgvector.Vector value)
            {
                throw new ArgumentException("columnValue must be a PgVector");
            }
            var name = "@myp" + columnIndex;

            return ToSugarParameter(value, $"@vector_p_{columnIndex}");
        }

        /// <summary>
        /// 读取转换：从数据记录中取出向量列原始值并强转为目标类型 T。
        /// 实现 ISugarDataConverter 接口，向量列查询时由 SqlSugar 回调。
        /// </summary>
        /// <typeparam name="T">目标转换类型</typeparam>
        /// <param name="dataRecord">当前行数据记录</param>
        /// <param name="dataRecordIndex">列索引</param>
        /// <returns>转换后的列值</returns>
        public T QueryConverter<T>(IDataRecord dataRecord, int dataRecordIndex)
        {
            var value = dataRecord.GetValue(dataRecordIndex);
            return (T)Convert.ChangeType(value, typeof(T));
        }

        /// <summary>
        /// 把 Pgvector.Vector 包装为 DbType.Object 的 SqlSugar 参数，
        /// 实际序列化由 Pgvector 的 Npgsql 插件完成。
        /// </summary>
        /// <param name="vector">向量值</param>
        /// <param name="name">SQL 参数名（如 @vector_p_0）</param>
        /// <returns>SqlSugar 参数对象</returns>
        private SugarParameter ToSugarParameter(Pgvector.Vector vector, string name)
        {
            return new SugarParameter(name, null)
            {
                Value = vector,
                DbType = System.Data.DbType.Object
            };
        }
    }
}
