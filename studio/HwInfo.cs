using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// HWiNFO64 共享内存读取器 (.NET Framework)
// 布局参考: https://gist.github.com/namazso/0c37be5a53863954c8c8279f66cfb1cc
// 要求: HWiNFO64 运行中, 且设置里勾选 "Shared Memory Support", 传感器窗口打开
internal class HwEntry
{
    public const int TypeTemp = 1;   // 温度
    public const int TypeFan = 3;    // 风扇
    public const int TypeUsage = 7;  // 占用
    public string Sensor;     // 所属传感器名, 如 "CPU"
    public string Reading;    // 读数名, 如 "CPU Fan"
    public string Unit;       // 单位, 如 "RPM" / "°C"
    public int Type;          // 1=温度 2=电压 3=风扇 4=电流 5=功率 6=频率 7=占用
    public double Value;

    public string Key() { return "h|" + Sensor + "|" + Reading; }
}

internal static class HwInfo
{
    public const string MapName = "Global\\HWiNFO_SENS_SM2";
    private const uint FILE_MAP_READ = 4;
    private const uint MAGIC = 0x53695748;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenFileMappingW(uint dwDesiredAccess, bool bInheritHandle, string lpName);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);
    [DllImport("kernel32.dll")]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll")]
    private static extern UIntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public uint Padding1;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type_;
        public uint Padding2;
    }

    public enum State { NotRunning, NoSharedMemory, Ok }

    public static bool IsRunning()
    {
        Process[] ps = Process.GetProcessesByName("HWiNFO64");
        bool r = ps.Length > 0;
        foreach (Process p in ps) p.Dispose();
        return r;
    }

    /// <summary>读取全部读数; 返回 false 表示当前没有可读数据</summary>
    public static bool TryRead(out List<HwEntry> items, out State state)
    {
        items = new List<HwEntry>();
        state = IsRunning() ? State.NoSharedMemory : State.NotRunning;

        IntPtr h = OpenFileMappingW(FILE_MAP_READ, false, MapName);
        if (h == IntPtr.Zero) return false;

        IntPtr ptr = MapViewOfFile(h, FILE_MAP_READ, 0, 0, UIntPtr.Zero);
        if (ptr == IntPtr.Zero) { CloseHandle(h); return false; }

        try
        {
            MEMORY_BASIC_INFORMATION mbi;
            if (VirtualQuery(ptr, out mbi, (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == UIntPtr.Zero)
                return false;
            long region = (long)mbi.RegionSize;
            if (region < 48) return false;

            if ((uint)Marshal.ReadInt32(ptr, 0) != MAGIC) return false;

            int sensorOff = Marshal.ReadInt32(ptr, 0x14);
            int sensorSize = Marshal.ReadInt32(ptr, 0x18);
            int sensorCount = Marshal.ReadInt32(ptr, 0x1C);
            int entryOff = Marshal.ReadInt32(ptr, 0x20);
            int entrySize = Marshal.ReadInt32(ptr, 0x24);
            int entryCount = Marshal.ReadInt32(ptr, 0x28);

            if (sensorSize < 264 || entrySize < 316) return false;
            if (sensorCount < 1 || sensorCount > 4096) return false;
            if (entryCount < 1 || entryCount > 65536) return false;
            if (sensorOff < 48 || entryOff < 48) return false;
            if ((long)sensorOff + (long)sensorCount * sensorSize > region) return false;
            if ((long)entryOff + (long)entryCount * entrySize > region) return false;

            // HWiNFO 按系统 ANSI 代码页写字符串(中文系统=GBK, 西文=1252)
            Encoding ansi = Encoding.Default;

            // 传感器名表
            List<string> names = new List<string>();
            for (int i = 0; i < sensorCount; i++)
            {
                int b = sensorOff + i * sensorSize;
                string user = ReadStr(ptr, b + 136, 128, ansi);
                string orig = ReadStr(ptr, b + 8, 128, ansi);
                names.Add(user.Length > 0 ? user : orig);
            }

            for (int i = 0; i < entryCount; i++)
            {
                int b = entryOff + i * entrySize;
                int type = Marshal.ReadInt32(ptr, b);
                int idx = Marshal.ReadInt32(ptr, b + 4);
                string rOrig = ReadStr(ptr, b + 12, 128, ansi);
                string rUser = ReadStr(ptr, b + 140, 128, ansi);
                string unit = ReadStr(ptr, b + 268, 16, ansi);
                double value = BitConverter.ToDouble(ReadBytes(ptr, b + 284, 8), 0);
                string sensor = (idx >= 0 && idx < names.Count) ? names[idx] : ("s_" + idx);
                HwEntry e = new HwEntry();
                e.Type = type;
                e.Sensor = sensor;
                e.Reading = rUser.Length > 0 ? rUser : rOrig;
                e.Unit = unit;
                e.Value = value;
                items.Add(e);
            }
            state = State.Ok;
            return true;
        }
        finally
        {
            UnmapViewOfFile(ptr);
            CloseHandle(h);
        }
    }

    private static string ReadStr(IntPtr ptr, int off, int len, Encoding enc)
    {
        byte[] raw = ReadBytes(ptr, off, len);
        int n = Array.IndexOf(raw, (byte)0);
        if (n < 0) n = raw.Length;
        string s = enc.GetString(raw, 0, n);
        return s;
    }

    private static byte[] ReadBytes(IntPtr ptr, int off, int len)
    {
        byte[] b = new byte[len];
        Marshal.Copy(ptr + off, b, 0, len);
        return b;
    }
}
