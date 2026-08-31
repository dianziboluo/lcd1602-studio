using System;
using System.Collections.Generic;
using System.Diagnostics;
using LibreHardwareMonitor.Hardware;

// 数据引擎: 内置 LHM(占用/核显温度/内存/可选风扇) + 系统计数器兜底 + 可选 HWiNFO(风扇/CPU 真实温度等详细项)
internal class DataEngine : IDisposable
{
    private Computer _comp;
    private ISensor _cpuLoad, _memLoad, _socTemp, _gpuLoad, _lhmFan;
    private bool _lhmOpen;
    private PerformanceCounter _pcCpu, _pcMem;
    private bool _countersOk;

    public bool LhmOk { get { return _lhmOpen; } }
    public int CpuPct = -1, RamPct = -1, SocC = -1, GpuPct = -1;
    public int FanRpm = -1;      // 风扇: LHM 优先, 其次 HWiNFO
    public int CpuRealC = -1;    // CPU 真实温度(需 HWiNFO)
    public List<HwEntry> HwItems = new List<HwEntry>();
    public int HwCount { get { return HwItems.Count; } }
    public string HwStateText = "未检测";

    public void Open()
    {
        try
        {
            _comp = new Computer();
            _comp.IsCpuEnabled = true;
            _comp.IsGpuEnabled = true;
            _comp.IsMemoryEnabled = true;
            _comp.IsMotherboardEnabled = true;
            _comp.Open();
            ISensor soc = null, amd = null, any = null;
            foreach (IHardware hw in _comp.Hardware)
            {
                if (hw.HardwareType == HardwareType.Cpu)
                    _cpuLoad = Find(hw, SensorType.Load, "CPU Total");
                else if (hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuNvidia
                         || hw.HardwareType == HardwareType.GpuIntel)
                {
                    // 核显温度选择: 优先名字含 SoC 的读数, 其次 AMD 核显, 最后任意核显
                    ISensor best = FindSocTemp(hw);
                    if (best != null)
                    {
                        if (soc == null) soc = best;
                        if (hw.HardwareType == HardwareType.GpuAmd && amd == null) amd = best;
                    }
                    if (any == null && best != null) any = best;
                    if (hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuIntel)
                        if (_gpuLoad == null) _gpuLoad = Find(hw, SensorType.Load, "GPU Core");
                }
                else if (hw.HardwareType == HardwareType.Memory && hw.Name == "Total Memory")
                    _memLoad = Find(hw, SensorType.Load, "Memory");
                foreach (IHardware sub in hw.SubHardware)
                    if (_lhmFan == null)
                        _lhmFan = FindAnyFan(sub);
                if (_lhmFan == null) _lhmFan = FindAnyFan(hw);
            }
            _socTemp = soc != null ? soc : (amd != null ? amd : any);
            _lhmOpen = true;
        }
        catch { _lhmOpen = false; }

        // 系统计数器兜底(仅 LHM 不可用时使用)
        if (!_lhmOpen)
        {
            try
            {
                _pcCpu = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _pcMem = new PerformanceCounter("Memory", "% Committed Bytes In Use");
                _pcCpu.NextValue();
                _pcMem.NextValue();
                _countersOk = true;
            }
            catch { }
        }
    }

    private static int ClampPct(int v) { return v < 0 ? -1 : (v > 100 ? 100 : v); }

    private static ISensor Find(IHardware hw, SensorType t, string name)
    {
        foreach (ISensor s in hw.Sensors)
            if (s.SensorType == t && s.Name == name) return s;
        return null;
    }

    private static ISensor FindSocTemp(IHardware hw)
    {
        ISensor best = null;
        string hn = hw.Name.ToLowerInvariant();
        foreach (ISensor s in hw.Sensors)
        {
            if (s.SensorType != SensorType.Temperature || !s.Value.HasValue) continue;
            if (s.Name == "GPU VR SoC") return s;
            // 只把"像核显"的 GPU 留给 {soc}; 独显(RX/RTX 等)不参与兜底
            if (hn.Contains("rx") || hn.Contains("rtx") || hn.Contains("geforce") || hn.Contains("quadro")) continue;
            if (best == null || s.Value.Value > best.Value.Value) best = s;
        }
        return best;
    }

    private static ISensor FindAnyFan(IHardware hw)
    {
        foreach (ISensor s in hw.Sensors)
            if (s.SensorType == SensorType.Fan && s.Value.HasValue && s.Value.Value > 0) return s;
        return null;
    }

    private static int Val(ISensor s)
    {
        if (s == null || !s.Value.HasValue) return -1;
        int v = (int)Math.Round(s.Value.Value);
        return v < 0 ? -1 : v;
    }

    /// <summary>每秒调用: 刷新全部数据</summary>
    public void Tick()
    {
        if (_lhmOpen)
        {
            try
            {
                foreach (IHardware hw in _comp.Hardware)
                {
                    hw.Update();
                    foreach (IHardware sub in hw.SubHardware) sub.Update();
                }
            }
            catch { }
        }
        CpuPct = Val(_cpuLoad);
        RamPct = Val(_memLoad);
        SocC = Val(_socTemp);
        GpuPct = _gpuLoad != null ? Val(_gpuLoad) : -1;

        // LHM 不可用时用系统计数器兜底
        if ((CpuPct < 0 || RamPct < 0) && _countersOk)
        {
            try
            {
                int c = (int)Math.Round(_pcCpu.NextValue());
                int m = (int)Math.Round(_pcMem.NextValue());
                if (CpuPct < 0) CpuPct = ClampPct(c);
                if (RamPct < 0) RamPct = ClampPct(m);
            }
            catch { }
        }

        // HWiNFO
        List<HwEntry> items;
        HwInfo.State st;
        if (HwInfo.TryRead(out items, out st))
        {
            HwItems = items;
            HwStateText = "HWiNFO ● " + items.Count + " 项读数";
        }
        else if (st == HwInfo.State.NoSharedMemory)
        {
            HwItems.Clear();
            HwStateText = "HWiNFO 已运行,但共享内存未开启";
        }
        else
        {
            HwItems.Clear();
            HwStateText = "HWiNFO 未运行";
        }

        FanRpm = (_lhmFan != null && _lhmFan.Value.HasValue) ? Val(_lhmFan) : -1;
        CpuRealC = -1;
        if (st == HwInfo.State.Ok)
        {
            // 风扇: 找第一个风扇读数
            if (FanRpm < 0) FanRpm = PickFan(items);
            CpuRealC = PickCpuTemp(items);
        }
    }

    private static int PickFan(List<HwEntry> items)
    {
        int best = -1;
        foreach (HwEntry e in items)
        {
            if (e.Type != HwEntry.TypeFan) continue;
            if (e.Value <= 0) continue;
            if (best < 0) best = (int)Math.Round(e.Value);
            else best = Math.Max(best, (int)Math.Round(e.Value));
        }
        return best;
    }

    private static int PickCpuTemp(List<HwEntry> items)
    {
        // 优先 CPU 传感器下的 Package / Tctl / Tdie; 其次 Sensor 名含 CPU 的第一个温度
        HwEntry fallback = null;
        foreach (HwEntry e in items)
        {
            if (e.Type != HwEntry.TypeTemp) continue;
            string sen = e.Sensor.ToLowerInvariant();
            string r = e.Reading.ToLowerInvariant();
            if (!sen.Contains("cpu")) continue;
            if (fallback == null) fallback = e;
            if (r.Contains("package") || r.Contains("tctl") || r.Contains("tdie"))
                return (int)Math.Round(e.Value);
        }
        return fallback != null ? (int)Math.Round(fallback.Value) : -1;
    }

    /// <summary>按 key("h|Sensor|Reading")取 HWiNFO 变量, 渲染时带单位</summary>
    public string FindHw(string key)
    {
        foreach (HwEntry e in HwItems)
            if (e.Key() == key)
            {
                string u = e.Unit == null ? "" : e.Unit.Trim();
                return FmtNum(e.Value) + (u.Length > 0 ? " " + u : "");
            }
        return null;
    }

    public static string FmtNum(double v)
    {
        if (Math.Abs(v) >= 100) return ((int)Math.Round(v)).ToString();
        return v.ToString("0.#");
    }

    public void Dispose()
    {
        if (_comp != null) { try { _comp.Close(); } catch { } }
        _comp = null;
    }
}
