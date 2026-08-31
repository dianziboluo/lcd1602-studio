// 电脑状态监视器 - PC 端(低资源占用)
// 用 LibreHardwareMonitorLib 读取 CPU/核显/风扇/内存数据,通过 USB 串口发送给 ESP32-C3
// 发送格式(每 1 秒一行):  C:<cpu占用>;S:<soc温度>;F:<风扇转速>;M:<内存占用>
//   例: C:32;S:45;F:3200;M:61      (任何值 -1 表示读不到)
//
// 用法:
//   monitor.exe               自动找串口并发送
//   monitor.exe --port=COM5   指定串口
//   monitor.exe --test        只打印一次读数,不发送(用于验证传感器)
//
// 编译(Windows 自带 .NET Framework,无需任何安装):
//   build_monitor.bat

using System;
using System.IO.Ports;
using System.Management;
using System.Threading;
using LibreHardwareMonitor.Hardware;

class Monitor
{
    const int IntervalMs = 1000;

    static Computer comp;
    static ISensor cpuLoad, socTemp, fan, memLoad;

    static void Main(string[] args)
    {
        string port = null;
        bool test = false;
        foreach (string a in args)
        {
            if (a == "--test") test = true;
            else if (a.StartsWith("--port=")) port = a.Substring(7);
        }

        comp = new Computer();
        comp.IsCpuEnabled = true;
        comp.IsGpuEnabled = true;
        comp.IsMotherboardEnabled = true;
        comp.IsMemoryEnabled = true;
        comp.Open();
        FindSensors();

        if (test)
        {
            UpdateAll();
            Console.WriteLine("CPU=" + FmtLoad(cpuLoad) + "  SOC=" + FmtTempInt(socTemp)
                + "  风扇=" + FmtLoad(fan) + "  内存=" + FmtLoad(memLoad));
            comp.Close();
            return;
        }

        if (port == null) port = FindPort();
        if (port == null)
        {
            Console.WriteLine("没有找到串口。请确认 ESP32 已插上 USB,或用 --port=COMx 指定。");
            comp.Close();
            return;
        }

        SerialPort sp = new SerialPort(port, 115200, Parity.None, 8, StopBits.One);
        sp.WriteTimeout = 1000;
        try { sp.Open(); }
        catch (Exception ex)
        {
            Console.WriteLine("无法打开串口 " + port + ": " + ex.Message);
            comp.Close();
            return;
        }
        Console.WriteLine("串口 " + port + " 已打开,每秒发送数据...(Ctrl+C 退出)");

        string last = "";
        int next = Environment.TickCount;
        for (;;)
        {
            Thread.Sleep(20);
            int now = Environment.TickCount;
            if (now - next < IntervalMs) continue;
            next = now + IntervalMs;

            UpdateAll();
            string line = string.Format("C:{0};S:{1};F:{2};M:{3}",
                SafeLoad(cpuLoad), SafeTempInt(socTemp), SafeLoad(fan), SafeLoad(memLoad));
            if (line == last) continue;   // 数据没变就不重复发,省一点点
            last = line;
            try
            {
                sp.WriteLine(line);
            }
            catch (Exception ex)
            {
                Console.WriteLine("串口写入失败: " + ex.Message + " (检查线缆后重试)");
                Thread.Sleep(1000);
            }
        }
    }

    // ------- 传感器 -------

    static void FindSensors()
    {
        foreach (IHardware hw in comp.Hardware)
        {
            CheckFan(hw);
            if (hw.HardwareType == HardwareType.Cpu)
            {
                cpuLoad = Find(hw, SensorType.Load, "CPU Total");
            }
            else if (hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuNvidia
                     || hw.HardwareType == HardwareType.GpuIntel)
            {
                socTemp = FindSocTemp(hw);   // 核显温度当作 SoC 温度(CPU 温度代理)
            }
            else if (hw.HardwareType == HardwareType.Memory && hw.Name == "Total Memory")
            {
                memLoad = Find(hw, SensorType.Load, "Memory");
            }
            foreach (IHardware sub in hw.SubHardware) CheckFan(sub);
        }
    }

    static ISensor Find(IHardware hw, SensorType type, string name)
    {
        foreach (ISensor s in hw.Sensors)
            if (s.SensorType == type && s.Name == name) return s;
        return null;
    }

    static ISensor FindSocTemp(IHardware hw)
    {
        ISensor best = null;
        foreach (ISensor s in hw.Sensors)
        {
            if (s.SensorType != SensorType.Temperature || !s.Value.HasValue) continue;
            if (s.Name == "GPU VR SoC") return s;          // 首选: SoC 核显温度
            if (best == null || s.Value.Value > best.Value.Value) best = s;
        }
        return best;
    }

    static void CheckFan(IHardware hw)
    {
        if (fan != null) return;
        foreach (ISensor s in hw.Sensors)
            if (s.SensorType == SensorType.Fan && s.Value.HasValue && s.Value.Value > 0)
            { fan = s; return; }
    }

    static void UpdateAll()
    {
        foreach (IHardware hw in comp.Hardware)
        {
            hw.Update();
            foreach (IHardware sub in hw.SubHardware) sub.Update();
        }
    }

    static int SafeTempInt(ISensor s)
    {
        if (s == null || !s.Value.HasValue) return -1;
        int v = (int)Math.Round(s.Value.Value);
        return v > 0 ? v : -1;   // 0 或读不到 -> -1
    }

    static int SafeLoad(ISensor s)
    {
        if (s == null || !s.Value.HasValue) return -1;
        int v = (int)Math.Round(s.Value.Value);
        return v < 0 ? -1 : v;
    }

    static string FmtTempInt(ISensor s) { int v = SafeTempInt(s); return v < 0 ? "--" : v + "C"; }
    static string FmtLoad(ISensor s) { int v = SafeLoad(s); return v < 0 ? "--" : v + "%"; }

    // ------- 串口识别(自动找 ESP32)-------

    static string FindPort()
    {
        try
        {
            // 优先找 Espressif(VID_303A)/CH340(VID_1A86)/CP210x(VID_10C4) 的串口设备
            foreach (ManagementObject mo in new ManagementObjectSearcher(
                "SELECT Name,PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'").Get())
            {
                string id = mo["PNPDeviceID"] as string;
                if (id == null) continue;
                if (id.IndexOf("VID_303A", StringComparison.OrdinalIgnoreCase) >= 0
                    || id.IndexOf("VID_1A86", StringComparison.OrdinalIgnoreCase) >= 0
                    || id.IndexOf("VID_10C4", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string p = PortFromName(mo["Name"] as string);
                    if (p != null) return p;
                }
            }
            // 退而求其次:任意串口
            foreach (string s in SerialPort.GetPortNames()) return s;
        }
        catch { }
        return null;
    }

    static string PortFromName(string name)
    {
        if (name == null) return null;
        int i = name.IndexOf("(COM");
        if (i < 0) return null;
        int j = name.IndexOf(')', i);
        if (j < 0) return null;
        return name.Substring(i + 1, j - i - 1);
    }
}
