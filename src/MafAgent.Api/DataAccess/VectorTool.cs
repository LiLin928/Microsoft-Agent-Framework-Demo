using Newtonsoft.Json;
using Pgvector;
using SqlSugar;
using System.Data;
using System.Text;
using Yitter.IdGenerator;

namespace MafAgent.Api.DataAccess
{
    /// <summary>
    /// 向量库帮助类
    /// </summary>
    public class VectorTool
    {

        private static string embeddingurl = DatabaseConfig.embeddingConfig.endpoint;

        private static string embeddingApikey = DatabaseConfig.embeddingConfig.apikey; 

        private static string embeddingmodel = DatabaseConfig.embeddingConfig.model;  //仅支持1024维度的模型

        private static string rerankurl = DatabaseConfig.rerankConfig.endpoint;
                              
        private static string rerankApikey = DatabaseConfig.rerankConfig.apikey;  
                            
        private static string rerankmodel = DatabaseConfig.rerankConfig.model; 


        /// <summary>
        /// 获取向量库列表
        /// </summary>
        /// <param name="title">库名称</param>
        /// <returns>返回列表Json</returns>
        public static (bool issuccess, string id) CreateVectorDb(string title)
        {
            try
            {
                VectorListEntity vectorListEntity = new VectorListEntity();
                vectorListEntity.id = YitIdHelper.NextId().ToString();
                vectorListEntity.title = title;

                var data = VectorFactory.BaseVector().Insertable(vectorListEntity).ExecuteCommand();

                return (true, vectorListEntity.id);
            }
            catch (Exception ex)
            {

                return (false,ex.Message);
            }      
        }


        /// <summary>
        /// 删除向量库列表
        /// </summary>
        /// <param name="id">库id</param>
        /// <returns>返回列表Json</returns>
        public static bool DelVectorDb(string id)
        {
            try
            {

                VectorFactory.BaseVector().Deleteable<VectorListEntity>(id).ExecuteCommand();

                return true;
            }
            catch (Exception ex)
            {

                return false;
            }
        }

        /// <summary>
        /// 添加/更新向量文档：正文超 8000 字自动分段，逐段向量化后入库，同批分段共用一个 groupid。
        /// </summary>
        /// <param name="id">文档组 id（groupid）：新增时传空串；更新时传上次 InsertVector 返回的 groupid，入库后会删除该组旧分段</param>
        /// <param name="dbid">所属知识库 id（CreateVectorDb 返回的 id，存入 parentid 字段）</param>
        /// <param name="title">文档标题</param>
        /// <param name="content">文档正文（超 8000 字符自动切分，每段各自调 embedding 模型）</param>
        /// <returns>issuccess + 文档组 id（groupid，更新/删除文档时回传它）</returns>
        public static (bool issuccess, string? id) InsertVector(string? id,string dbid, string title,string content)
        {
            try
            {
                var groupid = YitIdHelper.NextId().ToString();
                
                var textlist= SplitString(content, 8000);
                foreach (var item in textlist)
                {
                    var embedding = AddAITextEmbedding(item).Result;
                    if (!embedding.isuccess)
                    {
                        return (false, id);
                    }

                    VectorEntity ent = new VectorEntity();
                    ent.id = YitIdHelper.NextId().ToString();
                    ent.groupid = groupid;
                    ent.parentid = dbid;
                    ent.title = title;
                    ent.content = item;
                    ent.embedding = new Vector(embedding.data.data[0].embedding.ToArray());

                    VectorFactory.BaseVector().Insertable(ent).ExecuteCommand();

                   
                }
                if (!string.IsNullOrWhiteSpace(id))
                {
                    VectorFactory.BaseVector().Deleteable<VectorEntity>().Where(t => t.groupid == id).ExecuteCommand();
                }

                return (true,groupid);
            }
            catch (Exception ex)
            {

                return (false, id);
            }
        }


        /// <summary>
        /// 删除向量
        /// </summary>
        /// <param name="id">向量id</param>
        /// <returns>返回列表Json</returns>
        public static bool  DelVector(string id)
        {
            try
            {
   

                VectorFactory.BaseVector().Deleteable<VectorEntity>().Where(t => t.groupid == id).ExecuteCommand();

                return true;
            }
            catch (Exception ex)
            {

                return false;
            }
        }


        /// <summary>
        /// 检索向量
        /// </summary>
        /// <param name="dbid">库id</param>
        /// <param name="content">检索内容</param>
        /// <param name="top">返回数量</param>
        /// <returns>返回列表Json</returns>
        public static (bool issuccess, List<VectorEntity> data) selectVector(string dbid,string content,int top=2, bool Rank = false)
        {
            try
            {
                
                var embedding = AddAITextEmbedding(content).Result;
                if (!embedding.isuccess)
                {
                    return (false, new List<VectorEntity>());
                }
                var embed = new Vector(embedding.data.data[0].embedding.ToArray());
                List<SugarParameter> parameters =new List<SugarParameter>();
                parameters.Add(new SugarParameter("@embedding", null)
                {
                    Value = embed,
                    DbType = System.Data.DbType.Object
                });


                var list= VectorFactory.BaseVector().Queryable<VectorEntity>().Where(t => t.parentid == dbid ).AddParameters(parameters.ToArray()).Select("*,(embedding <#> @embedding) as Similarity").OrderBy("Similarity").Take(top).ToList();
                if (Rank)
                {
                    list = ToRerank(content, list).Result;
                }
                return (true,list);
            }
            catch (Exception ex)
            {

                return (false, new List<VectorEntity>());
            }
        }



        /// <summary>
        /// 文本转换为向量
        /// </summary>
        /// <param name="text">内容</param>
        /// <returns></returns>
        private static async Task<(bool isuccess, EmbeddingResponse data)> AddAITextEmbedding(string text)
        {
            var data = new EmbeddingResponse();
            string url = $"{embeddingurl}/embeddings";

            // 请求数据
            var requestData = new
            {
                model = embeddingmodel,
                input = text,
                encoding_format = "float"
            };

            // 将请求数据序列化为 JSON
            string jsonData = JsonConvert.SerializeObject(requestData);

            // 创建 HttpClient 实例
            using HttpClient client = new HttpClient();

            // 设置请求头
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {embeddingApikey}");

            // 创建 HttpContent
            HttpContent content = new StringContent(jsonData, Encoding.UTF8, "application/json");

            try
            {
                // 发送 POST 请求
                HttpResponseMessage response = await client.PostAsync(url, content);

                // 确保响应成功
                response.EnsureSuccessStatusCode();

                // 读取响应内容
                string responseBody = await response.Content.ReadAsStringAsync();
                data = JsonConvert.DeserializeObject<EmbeddingResponse>(responseBody);

                return (true, data);
                // 输出响应
              //  Console.WriteLine($"Usage - Prompt Tokens: {data.usage.promptTokens}");
              //  Console.WriteLine($"Usage - Completion Tokens: {data.usage.completionTokens}");
              //  Console.WriteLine($"Usage - Total Tokens: {data.usage.totalTokens}");
            }
            catch (HttpRequestException e)
            {
                return (false, new EmbeddingResponse());
                // 捕获并处理异常
               // Console.WriteLine("\nException Caught!");
               // Console.WriteLine("Message :{0} ", e.Message);
            }
        }


                /// <summary>
        /// 对召回的向量结果做重排（Rerank）：调用重排模型按 query 相关度重新打分排序。
        /// 成功时返回按相关度更新 Similarity 后的新列表；失败时原样返回入参列表。
        /// </summary>
        /// <param name="text">用户查询文本</param>
        /// <param name="list">待重排的向量召回结果</param>
        /// <returns>重排后的向量实体列表</returns>
private static async Task<List<VectorEntity>> ToRerank(string text, List<VectorEntity> list)
        {
            List<VectorEntity> retlist = new List<VectorEntity> { };
            var ret = await RerankEmbedding(text, list);
            if (ret.isuccess)
            {
                var rlist = ret.data.results;
                foreach (var item in rlist)
                {
                    var t = list[item.index];
                    t.Similarity = item.relevance_score;

                    retlist.Add(t);
                }
                return retlist;

            }
            else
            {

                return list;
            }






        }
        /// <summary>
        /// 重排向量
        /// </summary>
        /// <param name="text">内容</param>
        /// <returns></returns>
        private static async Task<(bool isuccess, RerankResponse data)> RerankEmbedding(string text, List<VectorEntity> list)
        {
            var data = new RerankResponse();
            string url = $"{rerankurl}/rerank";

            // 请求数据
            var requestData = new
            {
                model = rerankmodel,
                query = text,
                documents = list.Select(t => t.content).ToList(),
                top_n = list.Count,
                return_documents = false,
                max_chunks_per_doc = 1024,
                overlap_tokens = 80
            };

            // 将请求数据序列化为 JSON
            string jsonData = JsonConvert.SerializeObject(requestData);

            // 创建 HttpClient 实例
            using HttpClient client = new HttpClient();

            // 设置请求头
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {rerankApikey}");

            // 创建 HttpContent
            HttpContent content = new StringContent(jsonData, Encoding.UTF8, "application/json");

            try
            {
                // 发送 POST 请求
                HttpResponseMessage response = await client.PostAsync(url, content);

                // 确保响应成功
                response.EnsureSuccessStatusCode();

                // 读取响应内容
                string responseBody = await response.Content.ReadAsStringAsync();
                data = JsonConvert.DeserializeObject<RerankResponse>(responseBody);

                return (true, data);
                // 输出响应
                //  Console.WriteLine($"Usage - Prompt Tokens: {data.usage.promptTokens}");
                //  Console.WriteLine($"Usage - Completion Tokens: {data.usage.completionTokens}");
                //  Console.WriteLine($"Usage - Total Tokens: {data.usage.totalTokens}");
            }
            catch (HttpRequestException e)
            {
                return (false, data);
                // 捕获并处理异常
                // Console.WriteLine("\nException Caught!");
                // Console.WriteLine("Message :{0} ", e.Message);
            }
        }


                /// <summary>
        /// 把长文本按固定长度切分为多段（默认每段 8000 字符），
        /// 用于超长文档入向量库前的分块嵌入。
        /// </summary>
        /// <param name="input">原始长文本</param>
        /// <param name="chunkSize">每段最大字符数，默认 8000</param>
        /// <returns>切分后的文本片段列表</returns>
public static List<string> SplitString(string input, int chunkSize=8000)
        {
            List<string> result = new List<string>();
            for (int i = 0; i < input.Length; i += chunkSize)
            {
                result.Add(input.Substring(i, Math.Min(chunkSize, input.Length - i)));
            }
            return result;
        }
    }
}
public class EmbeddingResponse
{
    public string model { get; set; }
    public List<EmbeddingData> data { get; set; }
    public Usage usage { get; set; }
}

public class EmbeddingData
{
    public string Object { get; set; }
    public List<float> embedding { get; set; }
    public int index { get; set; }
}

public class RerankResponse
{
    public string id { get; set; }
    public List<Rerankresults> results { get; set; }
}


public class Rerankresults
{
    public int index { get; set; }
    public double relevance_score { get; set; }

}


public class Usage
{
    public int promptTokens { get; set; }
    public int completionTokens { get; set; }
    public int totalTokens { get; set; }
}


public class DataInfo
{
    public string id { get; set; }
    public string title { get; set; }
    public double Similarity { get; set; }
    public string text { get; set; }


}