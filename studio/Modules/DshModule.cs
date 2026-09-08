using System;
using System.Collections.Generic;
using System.IO;

// DSH 模块: 运行 / 空闲 / 未启动 (+ 距上次活动多少分钟)
// 判定(按设计确认):
//   1) 最新会话文件在 idleSeconds 内有写入  -> 运行中
//   2) 或 DSH 的 node 进程存在到 443 的已建立连接(覆盖静默生成期) -> 运行中
//   3) 进程在但都不满足 -> 空闲
//   4) 进程/端口(3080)都不在且长期无活动 -> 未启动
internal class DshModule : ModuleBase
{
    private string _home;
    private int _idleSec = 90;
    private string _state = "未启动";
    private long _lastActivityMs;

    public override string Id { get { return "dsh"; } }
    public override string Name { get { return "DSH"; } }
    public override int PollMs { get { return 3000; } }

    public override string StatusText
    {
        get { return Ok ? _state : Err; }
    }

    protected override void OnInit()
    {
        _home = Get("dshhome", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh"));
        _idleSec = GetInt("idleseconds", 90);
    }

    protected override void DoPoll()
    {
        HashSet<int> nodePids = TcpInfo.PidsOf("node");
        bool port3080 = TcpInfo.IsPortListening(3080);
        bool apiActive = nodePids.Count > 0 && TcpInfo.HasEstablished443(nodePids);

        long newest = NewestSessionMs();
        long ageSec = newest <= 0 ? long.MaxValue
            : (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - newest) / 1000;
        _lastActivityMs = newest;

        bool alive = port3080 || nodePids.Count > 0 || ageSec <= 600;
        if (!alive) _state = "未启动";
        else if (ageSec <= _idleSec || apiActive) _state = "运行中";
        else _state = "空闲";
    }

    /// <summary>最新会话记录文件的修改时间(毫秒); 只取时间, 不解析 zstd 内容</summary>
    private long NewestSessionMs()
    {
        string dir = Path.Combine(_home, "sessions");
        if (!Directory.Exists(dir)) return 0;
        long best = 0;
        try
        {
            foreach (string f in Directory.GetFiles(dir, "session.jsonl.zstd", SearchOption.AllDirectories))
            {
                long t = File.GetLastWriteTimeUtc(f).Ticks / TimeSpan.TicksPerMillisecond;
                if (t > best) best = t;
            }
        }
        catch { }
        return best;
    }

    private string IdleMinutes()
    {
        if (_lastActivityMs <= 0) return "--";
        long sec = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastActivityMs) / 1000;
        if (sec < 60) return "0";
        return ((int)(sec / 60)).ToString();
    }

    public override IEnumerable<ModuleVar> Variables()
    {
        List<ModuleVar> list = new List<ModuleVar>();
        list.Add(Make("dsh.state", "DSH 状态", "", delegate { return _state; }));
        list.Add(Make("dsh.min", "距上次活动", "min", IdleMinutes));
        return list;
    }

    private static ModuleVar Make(string token, string name, string unit, Func<string> fn)
    {
        ModuleVar v = new ModuleVar();
        v.Token = token; v.Name = name; v.Unit = unit; v.Value = fn;
        return v;
    }
}
