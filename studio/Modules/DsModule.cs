using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

// DeepSeek 模块: 账户余额 + 平价(空闲)时段倒计时
//   余额: 优先读 DSH 缓存(provider-snapshots.json, 零成本无需密钥),
//         缓存超过 cacheMaxAgeMin 分钟则调用官方接口 /user/balance 刷新
//   平价: 纯本地时钟计算。官方现行规则(可配置): 周一至周五 09:00-12:00 与 14:00-18:00 为高峰,
//         其余时间为空闲时段(价格五折)
internal class DsModule : ModuleBase
{
    private string _snapPath, _credPath, _apiKey;
    private int _cacheMaxAgeMin = 30;

    private string _bal = "--", _grant = "--", _top = "--";
    private DateTime _balStamp = DateTime.MinValue;
    private List<PeakWindow> _peaks;

    public override string Id { get { return "deepseek"; } }
    public override string Name { get { return "DeepSeek"; } }
    public override int PollMs { get { return 300000; } }   // 余额 5 分钟一次

    public override string StatusText
    {
        get { return Ok ? (IsHigh(DateTime.Now) ? "高峰" : "空闲") : Err; }
    }

    private class PeakWindow
    {
        public int DayFrom, DayTo;      // ISO: 1=周一 ... 7=周日
        public TimeSpan Start, End;
    }

    protected override void OnInit()
    {
        string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        _snapPath = Get("snapshotspath", Path.Combine(home, "dsh-usage", "provider-snapshots.json"));
        _credPath = Get("credentialspath", Path.Combine(home, ".credentials.yaml"));
        _apiKey = Get("apikey", "");
        _cacheMaxAgeMin = GetInt("cachemaxagemin", 30);
        _peaks = ParsePeaks(Get("peak", "1-5 09:00-12:00;1-5 14:00-18:00"));
    }

    protected override void DoPoll()
    {
        ReadCache();
        double ageMin = _balStamp == DateTime.MinValue ? double.MaxValue
            : (DateTime.Now - _balStamp).TotalMinutes;
        if (ageMin > _cacheMaxAgeMin) TryRefreshFromApi();
    }

    // ---------- 余额 ----------

    private void ReadCache()
    {
        if (!File.Exists(_snapPath)) return;
        try
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> root = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(_snapPath));
            Dictionary<string, object> providers = root["providers"] as Dictionary<string, object>;
            if (providers == null) return;
            Dictionary<string, object> ds = null;
            if (providers.ContainsKey("deepseek-official")) ds = providers["deepseek-official"] as Dictionary<string, object>;
            if (ds == null && providers.ContainsKey("deepseek")) ds = providers["deepseek"] as Dictionary<string, object>;
            if (ds == null) return;
            Dictionary<string, object> bal = ds.ContainsKey("balance") ? ds["balance"] as Dictionary<string, object> : null;
            if (bal == null) return;
            object tb;
            if (bal.TryGetValue("totalBalance", out tb) && tb != null)
                _bal = Convert.ToString(tb);
            object at;
            if (bal.TryGetValue("updatedAt", out at) && at != null)
            {
                double ms = Convert.ToDouble(at);
                if (ms > 1e11) _balStamp = DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime;
            }
        }
        catch { }
    }

    private void TryRefreshFromApi()
    {
        string key = _apiKey;
        if (key.Length == 0) key = KeyFromCredentials();
        if (key.Length == 0) return;
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://api.deepseek.com/user/balance");
            req.Method = "GET";
            req.Timeout = 8000;
            req.ReadWriteTimeout = 8000;
            req.Headers["Authorization"] = "Bearer " + key;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                string json = sr.ReadToEnd();
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = int.MaxValue;
                Dictionary<string, object> root = ser.Deserialize<Dictionary<string, object>>(json);
                IList<object> infos = AsList(root["balance_infos"]);
                if (infos != null && infos.Count > 0)
                {
                    Dictionary<string, object> b = infos[0] as Dictionary<string, object>;
                    if (b != null)
                    {
                        _bal = Str(b, "total_balance", _bal);
                        _grant = Str(b, "granted_balance", _grant);
                        _top = Str(b, "topped_up_balance", _top);
                        _balStamp = DateTime.Now;
                    }
                }
            }
        }
        catch { }
    }

    private string KeyFromCredentials()
    {
        try
        {
            if (!File.Exists(_credPath)) return "";
            string txt = File.ReadAllText(_credPath);
            Match m = Regex.Match(txt, @"DEEPSEEK_API_KEY\s*:\s*([A-Za-z0-9_\-\.]+)");
            return m.Success ? m.Groups[1].Value : "";
        }
        catch { return ""; }
    }

    private static string Str(Dictionary<string, object> d, string k, string def)
    {
        object v;
        if (d.TryGetValue(k, out v) && v != null) return Convert.ToString(v);
        return def;
    }

    // ---------- 平价时段 ----------

    private static List<PeakWindow> ParsePeaks(string spec)
    {
        List<PeakWindow> list = new List<PeakWindow>();
        if (spec == null) return list;
        foreach (string part in spec.Split(';'))
        {
            string s = part.Trim();
            if (s.Length == 0) continue;
            string[] two = s.Split(' ');
            if (two.Length != 2) continue;
            string[] days = two[0].Split('-');
            string[] times = two[1].Split('-');
            if (days.Length != 2 || times.Length != 2) continue;
            try
            {
                PeakWindow w = new PeakWindow();
                w.DayFrom = int.Parse(days[0]);
                w.DayTo = int.Parse(days[1]);
                w.Start = TimeSpan.Parse(times[0]);
                w.End = TimeSpan.Parse(times[1]);
                list.Add(w);
            }
            catch { }
        }
        return list;
    }

    private static int IsoDay(DateTime t)
    {
        int d = (int)t.DayOfWeek;
        return d == 0 ? 7 : d;
    }

    private bool IsHigh(DateTime now)
    {
        if (_peaks == null) return false;
        int iso = IsoDay(now);
        foreach (PeakWindow w in _peaks)
        {
            if (iso < w.DayFrom || iso > w.DayTo) continue;
            TimeSpan t = now.TimeOfDay;
            if (t >= w.Start && t < w.End) return true;
        }
        return false;
    }

    /// <summary>当前若在高峰, 返回本段高峰结束时刻(即下一个空闲时段开始)</summary>
    private DateTime? CurrentPeakEnd(DateTime now)
    {
        if (_peaks == null) return null;
        int iso = IsoDay(now);
        foreach (PeakWindow w in _peaks)
        {
            if (iso < w.DayFrom || iso > w.DayTo) continue;
            if (now.TimeOfDay >= w.Start && now.TimeOfDay < w.End)
                return now.Date.Add(w.End);
        }
        return null;
    }

    /// <summary>下一个高峰时段开始时刻(用于算“空闲还剩多久”)</summary>
    private DateTime NextHighStart(DateTime now)
    {
        if (_peaks == null) return now;
        DateTime best = DateTime.MaxValue;
        for (int d = 0; d <= 8; d++)
        {
            DateTime day = now.Date.AddDays(d);
            int iso = IsoDay(day);
            foreach (PeakWindow w in _peaks)
            {
                if (iso < w.DayFrom || iso > w.DayTo) continue;
                DateTime start = day.Add(w.Start);
                if (start > now && start < best) best = start;
            }
        }
        return best == DateTime.MaxValue ? now : best;
    }

    // ---------- 变量 ----------

    public override IEnumerable<ModuleVar> Variables()
    {
        List<ModuleVar> list = new List<ModuleVar>();
        list.Add(Make("ds.bal", "账户余额", "元", delegate { return _bal; }));
        list.Add(Make("ds.bal.grant", "赠金余额", "元", delegate { return _grant; }));
        list.Add(Make("ds.bal.top", "充值余额", "元", delegate { return _top; }));
        list.Add(Make("ds.bal.age", "余额数据年龄", "min", delegate
        {
            if (_balStamp == DateTime.MinValue) return "--";
            return ((int)(DateTime.Now - _balStamp).TotalMinutes).ToString();
        }));
        list.Add(Make("ds.peak.now", "当前时段", "", delegate { return IsHigh(DateTime.Now) ? "高峰" : "空闲"; }));
        list.Add(Make("ds.rate", "当前价率", "", delegate { return IsHigh(DateTime.Now) ? "全价" : "五折"; }));
        list.Add(Make("ds.peak.in", "距空闲时段开始", "", delegate
        {
            DateTime now = DateTime.Now;
            DateTime? end = CurrentPeakEnd(now);
            if (end == null) return "0m";                 // 已在空闲中
            return FmtDuration(end.Value - now);
        }));
        list.Add(Make("ds.peak.left", "空闲还剩", "", delegate
        {
            DateTime now = DateTime.Now;
            if (IsHigh(now)) return "--";
            return FmtDuration(NextHighStart(now) - now);
        }));
        return list;
    }

    private static ModuleVar Make(string token, string name, string unit, Func<string> fn)
    {
        ModuleVar v = new ModuleVar();
        v.Token = token; v.Name = name; v.Unit = unit; v.Value = fn;
        return v;
    }
}
