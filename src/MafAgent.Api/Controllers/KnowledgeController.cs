using Microsoft.AspNetCore.Mvc;
using MafAgent.Api.DataAccess;

namespace MafAgent.Api.Controllers;

/// <summary>知识库管理（迁移自 SK8 VoctorController，逻辑不变）</summary>
[ApiController]
[Route("/api/[controller]/[action]")]
public class KnowledgeController : Controller
{
    /// <summary>创建知识库</summary>
    [HttpPost]
    public retjson CreateVectorDb(string title)
    {
        retjson ret = new();
        try
        {
            var data = VectorTool.CreateVectorDb(title);
            ret.issuccess = data.issuccess;
            ret.message = data.id;
        }
        catch (Exception ex)
        {
            ret.issuccess = false;
            ret.message = ex.Message;
        }
        return ret;
    }

    /// <summary>删除知识库</summary>
    [HttpPost]
    public bool DelVectorDb(string id)
    {
        try { return VectorTool.DelVectorDb(id); }
        catch { return false; }
    }

    /// <summary>新增/修改文档（自动分段、向量化入库）</summary>
    [HttpPost]
    public retjson InsertVector(string dbid, string title, string content, string? id = null)
    {
        retjson ret = new();
        try
        {
            var data = VectorTool.InsertVector(id, dbid, title, content);
            ret.issuccess = data.issuccess;
            ret.message = data.id;
        }
        catch (Exception ex)
        {
            ret.issuccess = false;
            ret.message = ex.Message;
        }
        return ret;
    }

    /// <summary>删除文档</summary>
    [HttpPost]
    public bool DelVector(string id)
    {
        try
        {
            VectorFactory.BaseVector().Deleteable<VectorEntity>().Where(t => t.groupid == id).ExecuteCommand();
            return true;
        }
        catch { return false; }
    }

    /// <summary>语义检索</summary>
    [HttpPost]
    public retjson selectVector(string dbid, string content, int top = 2)
    {
        retjson ret = new();
        try
        {
            var data = VectorTool.selectVector(dbid, content, top);
            ret.issuccess = data.issuccess;
            ret.data = data.data;
        }
        catch (Exception ex)
        {
            ret.issuccess = false;
            ret.message = ex.Message;
        }
        return ret;
    }
}

/// <summary>统一返回（兼容 SK8）</summary>
public class retjson
{
    public bool issuccess { get; set; }
    public string? message { get; set; }
    public List<VectorEntity>? data { get; set; }
}

/// <summary>系统初始化（迁移自 SK8 DefaultController：建 pgvector 表）</summary>
[ApiController]
[Route("/api/[controller]/[action]")]
public class SystemController : Controller
{
    /// <summary>数据库初始化（创建 vector 扩展与表）</summary>
    [HttpPost]
    public ActionResult Install()
    {
        try
        {
            VectorFactory.BaseVector().Ado.ExecuteCommand("CREATE EXTENSION IF NOT EXISTS vector;");
        }
        catch
        {
            // 扩展可能已存在或由 DBA 管理
        }

        VectorFactory.BaseVector().Ado.ExecuteCommand("""
            CREATE TABLE IF NOT EXISTS public."Vector"(
                id text NOT NULL,
                parentid text NOT NULL,
                title text NOT NULL,
                content text NOT NULL,
                embedding vector(1024) NOT NULL,
                groupid text NOT NULL,
                "orgId" text,
                CONSTRAINT "Vector_pkey" PRIMARY KEY (id)
            );
            """);

        VectorFactory.BaseVector().Ado.ExecuteCommand("""
            CREATE TABLE IF NOT EXISTS public."VectorList"(
                id text NOT NULL,
                title text NOT NULL,
                CONSTRAINT "VectorList_pkey" PRIMARY KEY (id)
            );
            """);

        return Content("初始化完成");
    }
}
