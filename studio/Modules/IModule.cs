using System;
using System.Collections.Generic;

// 扩展模块框架: PC 端一切功能以模块形式接入, 显示器(ESP32)只负责显示
// 新增一个数据源 = 实现 IModule(建议继承 ModuleBase) + 在 MainForm 里注册一行

/// <summary>模块对外暴露的变量(模板中用 {Token} 引用)</summary>
internal class ModuleVar
{
    public string Token;        // 不含花括号, 如 "dsh.state"
    public string Name;         // 数据台显示名
    public string Unit;         // 单位(可空)
    public Func<string> Value;  // 取值(在 UI 线程调用, 应快速返回; 慢 IO 请放在 DoPoll)
}

internal interface IModule
{
    string Id { get; }              // 配置节名, 如 "dsh"
    string Name { get; }            // 数据台分组名
    int PollMs { get; }             // 轮询间隔
    bool Ok { get; }                // 当前是否正常
    string StatusText { get; }      // 状态条/徽章文字
    void Configure(IDictionary<string, string> cfg);
    void Poll();                    // 后台线程调用, 允许阻塞
    IEnumerable<ModuleVar> Variables();
}

/// <summary>模块基类: 统一异常隔离(任何异常只让本模块显示 --, 不影响主程序)</summary>
internal abstract class ModuleBase : IModule
{
    protected IDictionary<string, string> Cfg = new Dictionary<string, string>();
    protected string Err;

    public abstract string Id { get; }
    public abstract string Name { get; }
    public virtual int PollMs { get { return 3000; } }
    public bool Ok { get { return Err == null; } }
    public virtual string StatusText { get { return Ok ? "正常" : Err; } }

    public void Configure(IDictionary<string, string> cfg)
    {
        if (cfg != null) Cfg = cfg;
        OnInit();
    }

    protected virtual void OnInit() { }

    public void Poll()
    {
        try
        {
            DoPoll();
            Err = null;
        }
        catch (Exception ex)
        {
            Err = ex.Message;
            if (Err != null && Err.Length > 60) Err = Err.Substring(0, 60);
        }
    }

    protected abstract void DoPoll();
    public abstract IEnumerable<ModuleVar> Variables();

    protected string Get(string key, string def)
    {
        string v;
        return Cfg.TryGetValue(key, out v) && v != null && v.Length > 0 ? v : def;
    }

    protected int GetInt(string key, int def)
    {
        int v;
        return int.TryParse(Get(key, ""), out v) ? v : def;
    }

    protected static string FmtDuration(TimeSpan t)
    {
        if (t.Ticks < 0) t = TimeSpan.Zero;
        if (t.TotalDays >= 1) return ((int)t.TotalDays) + "d" + t.Hours + "h";
        if (t.TotalHours >= 1) return ((int)t.TotalHours) + "h" + t.Minutes + "m";
        return ((int)t.TotalMinutes) + "m";
    }

    /// <summary>JSON 数组兼容转换(JavaScriptSerializer 会给出 ArrayList 或 object[])</summary>
    protected static IList<object> AsList(object o)
    {
        object[] arr = o as object[];
        if (arr != null) return arr;
        System.Collections.IEnumerable e = o as System.Collections.IEnumerable;
        if (e == null) return null;
        List<object> list = new List<object>();
        foreach (object x in e) list.Add(x);
        return list;
    }
}
