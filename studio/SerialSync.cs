using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Management;

// 串口同步器: 把 GUI 渲染好的两行文本发给 ESP32+1602 (L0/L1 协议)
internal class SerialSync : IDisposable
{
    private SerialPort _sp;

    public bool Connected { get { return _sp != null && _sp.IsOpen; } }
    public string PortName { get { return _sp != null ? _sp.PortName : ""; } }

    public bool Connect()
    {
        Disconnect();
        string port = FindPort();
        if (port == null) return false;
        try
        {
            _sp = new SerialPort(port, 115200, Parity.None, 8, StopBits.One);
            _sp.WriteTimeout = 500;
            _sp.Open();
            return true;
        }
        catch { Disconnect(); return false; }
    }

    public void Disconnect()
    {
        if (_sp != null)
        {
            try { _sp.Close(); } catch { }
            _sp.Dispose();
            _sp = null;
        }
    }

    /// <summary>下发一行 (row=0/1, text 可为任意长度, 固件负责 16 列裁剪/滚动)</summary>
    public void SendRow(int row, bool scroll, string text)
    {
        if (!Connected) return;
        try
        {
            byte[] body = EncodeLine(row, scroll, text);
            _sp.Write(body, 0, body.Length);
        }
        catch { }
    }

    /// <summary>下发一个自定义槽位字形 (CG,<槽0-7>,<8行字节>)</summary>
    public void SendSlot(int slot)
    {
        if (!Connected || slot < 0 || slot > 7) return;
        try
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder("CG," + slot);
            foreach (byte b in CustomGlyphs.Slot[slot])
                sb.Append(",").Append((int)b);
            sb.Append("\n");
            _sp.Write(System.Text.Encoding.ASCII.GetBytes(sb.ToString()), 0, sb.Length);
        }
        catch { }
    }

    /// <summary>编码为字节流: 自定义槽位标记(1..8)→0x01..0x08, ℃→槽1, °→0xDF, 其余 ASCII</summary>
    internal static byte[] EncodeLine(int row, bool scroll, string text)
    {
        List<byte> b = new List<byte>(text.Length + 8);
        string hdr = "L" + row + "," + (scroll ? "1" : "0") + ",";
        foreach (char c in hdr) b.Add((byte)c);
        foreach (char c in text)
        {
            if (c >= 1 && c <= 8) b.Add((byte)c);        // {g1}~{g8} -> 槽字节(固件减1)
            else if (c == '\u2103') b.Add(0x02);         // ℃ -> 槽1
            else if (c == '\u00B0') b.Add(0xDF);         // °  -> ROM 度符号
            else if (c < 128) b.Add((byte)c);
            else b.Add((byte)'?');
        }
        b.Add((byte)'\n');
        return b.ToArray();
    }

    /// <summary>自动找 ESP32 串口: Espressif(303A)/CH340(1A86)/CP210x(10C4)</summary>
    public static string FindPort()
    {
        try
        {
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
            foreach (string s in SerialPort.GetPortNames()) return s;
        }
        catch { }
        return null;
    }

    private static string PortFromName(string name)
    {
        if (name == null) return null;
        int i = name.IndexOf("(COM");
        if (i < 0) return null;
        int j = name.IndexOf(')', i);
        if (j < 0) return null;
        return name.Substring(i + 1, j - i - 1);
    }

    public void Dispose() { Disconnect(); }
}
