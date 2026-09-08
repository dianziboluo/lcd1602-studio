using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

// 本机 TCP 连接表(P/Invoke iphlpapi), 用于判断 DSH 是否在运行/是否正在与 API 通信
// 失败时静默降级(返回空表), 不影响其它信号
internal class TcpRow
{
    public int Pid;
    public int LocalPort;
    public int RemotePort;
    public int State;      // 2=LISTEN, 5=ESTABLISHED
}

internal static class TcpInfo
{
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder,
                                                   int ulAf, int tableClass, int reserved);

    public static List<TcpRow> Get()
    {
        List<TcpRow> rows = new List<TcpRow>();
        int size = 0;
        uint ret = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return rows;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            ret = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret != 0) return rows;
            int count = Marshal.ReadInt32(buf);
            IntPtr p = buf + 4;
            for (int i = 0; i < count; i++)
            {
                TcpRow r = new TcpRow();
                r.State = Marshal.ReadInt32(p, 0);
                r.LocalPort = SwapPort(Marshal.ReadInt32(p, 8));
                r.RemotePort = SwapPort(Marshal.ReadInt32(p, 16));
                r.Pid = Marshal.ReadInt32(p, 20);
                rows.Add(r);
                p += 24;
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(buf); }
        return rows;
    }

    private static int SwapPort(int p)
    {
        return ((p & 0xFF) << 8) | ((p >> 8) & 0xFF);
    }

    /// <summary>是否存在由指定进程发起的到 443 的已建立连接</summary>
    public static bool HasEstablished443(ICollection<int> pids)
    {
        foreach (TcpRow r in Get())
        {
            if (r.State == 5 && r.RemotePort == 443 && pids.Contains(r.Pid)) return true;
        }
        return false;
    }

    public static bool IsPortListening(int port)
    {
        foreach (TcpRow r in Get())
            if (r.State == 2 && r.LocalPort == port) return true;
        return false;
    }

    public static HashSet<int> PidsOf(string processName)
    {
        HashSet<int> set = new HashSet<int>();
        try
        {
            foreach (Process p in Process.GetProcessesByName(processName))
            {
                set.Add(p.Id);
                p.Dispose();
            }
        }
        catch { }
        return set;
    }
}
