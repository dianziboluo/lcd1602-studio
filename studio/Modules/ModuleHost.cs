using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

// 模块宿主: 注册表 + 后台轮询线程 + modules.ini 配置
// 配置文件: %APPDATA%\LCD1602Studio\modules.ini
//   [dsh]
//   idleSeconds=90
//   [deepseek]
//   cacheMaxAgeMin=30
internal class ModuleHost : IDisposable
{
    private readonly List<IModule> _mods = new List<IModule>();
    private Thread _thread;
    private volatile bool _stop;
    private int[] _lastPoll;

    public IList<IModule> Modules { get { return _mods; } }

    public void Add(IModule m) { _mods.Add(m); }

    public string ConfigFile
    {
        get
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LCD1602Studio");
            Directory.CreateDirectory(d);
            return Path.Combine(d, "modules.ini");
        }
    }

    public void Start()
    {
        Dictionary<string, Dictionary<string, string>> cfg = LoadConfig();
        foreach (IModule m in _mods)
        {
            Dictionary<string, string> sec;
            if (!cfg.TryGetValue(m.Id, out sec)) sec = new Dictionary<string, string>();
            try { m.Configure(sec); } catch { }
        }
        _lastPoll = new int[_mods.Count];
        _thread = new Thread(Loop);
        _thread.IsBackground = true;
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stop)
        {
            int now = Environment.TickCount;
            for (int i = 0; i < _mods.Count; i++)
            {
                if (now - _lastPoll[i] >= _mods[i].PollMs || _lastPoll[i] == 0)
                {
                    _lastPoll[i] = now == 0 ? 1 : now;
                    try { _mods[i].Poll(); } catch { }
                }
            }
            Thread.Sleep(200);
        }
    }

    private Dictionary<string, Dictionary<string, string>> LoadConfig()
    {
        Dictionary<string, Dictionary<string, string>> map = new Dictionary<string, Dictionary<string, string>>();
        try
        {
            if (!File.Exists(ConfigFile)) return map;
            string cur = "";
            foreach (string raw in File.ReadAllLines(ConfigFile))
            {
                string ln = raw.Trim();
                if (ln.Length == 0 || ln.StartsWith("#") || ln.StartsWith(";")) continue;
                if (ln.StartsWith("[") && ln.EndsWith("]"))
                {
                    cur = ln.Substring(1, ln.Length - 2).Trim().ToLowerInvariant();
                    if (!map.ContainsKey(cur)) map[cur] = new Dictionary<string, string>();
                    continue;
                }
                int eq = ln.IndexOf('=');
                if (eq <= 0 || cur.Length == 0) continue;
                map[cur][ln.Substring(0, eq).Trim().ToLowerInvariant()] = ln.Substring(eq + 1).Trim();
            }
        }
        catch { }
        return map;
    }

    /// <summary>首次运行写一份带注释的示例配置(不覆盖已有文件)</summary>
    public void WriteSampleConfigIfMissing()
    {
        try
        {
            if (File.Exists(ConfigFile)) return;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# LCD1602 Studio 扩展模块配置 (改完保存, 重启软件生效)");
            sb.AppendLine();
            sb.AppendLine("[dsh]");
            sb.AppendLine("# DSH 家目录(默认 %USERPROFILE%\\.dsh)");
            sb.AppendLine("# dshHome=");
            sb.AppendLine("# 多久没有新活动算“空闲”(秒)");
            sb.AppendLine("idleSeconds=90");
            sb.AppendLine();
            sb.AppendLine("[deepseek]");
            sb.AppendLine("# DSH 缓存的 provider 快照(余额来源之一)");
            sb.AppendLine("# snapshotsPath=");
            sb.AppendLine("# 凭据文件(取 DEEPSEEK_API_KEY; 仅本地读取, 不上传)");
            sb.AppendLine("# credentialsPath=");
            sb.AppendLine("# 手填 API Key(留空则用凭据文件里的)");
            sb.AppendLine("# apiKey=");
            sb.AppendLine("# 缓存余额多久算过期(分钟), 超过则调用官方接口刷新");
            sb.AppendLine("cacheMaxAgeMin=30");
            sb.AppendLine("# 高峰时段(北京时间), 格式: 星期范围 起-止, 用 ; 分隔; 1=周一 7=周日");
            sb.AppendLine("# 官方现行规则: 周一至周五 09:00-12:00 与 14:00-18:00 为高峰, 其余为空闲(五折)");
            sb.AppendLine("peak=1-5 09:00-12:00;1-5 14:00-18:00");
            File.WriteAllText(ConfigFile, sb.ToString());
        }
        catch { }
    }

    public void Dispose()
    {
        _stop = true;
        try { if (_thread != null) _thread.Join(800); } catch { }
        _thread = null;
    }
}
