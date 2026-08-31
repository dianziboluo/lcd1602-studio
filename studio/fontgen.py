# -*- coding: utf-8 -*-
"""从 Adafruit GFX glcdfont.c 生成 C# 5x7 字模(ASCII 32..126)"""
import re
import urllib.request

url = "https://raw.githubusercontent.com/adafruit/Adafruit-GFX-Library/master/glcdfont.c"
text = urllib.request.urlopen(url).read().decode("utf-8")
bytes_ = [int(x, 16) for x in re.findall(r"0x([0-9A-Fa-f]{2})", text)]

# 每字符 5 字节;ASCII 32 的偏移 = 32*5
start = 32 * 5
count = 95 * 5
data = bytes_[start:start + count]
assert len(data) == count, len(data)

lines = []
for i in range(0, count, 24):
    chunk = data[i:i + 24]
    lines.append("            " + ", ".join("0x%02X" % b for b in chunk) + ",")
body = "\n".join(lines)

cs = """// 5x7 点阵字体(ASCII 32~126)
// 字形衍生自 Adafruit GFX glcdfont.c (BSD-3-Clause, Copyright Adafruit Industries)
// 布局: 每字符 5 字节, 每字节 = 一列, bit0 = 顶行
using System;

internal static class Font5x7
{
    public const int CharW = 5;   // 列数
    public const int CharH = 7;   // 行数(第 8 行恒为 0, 不绘制)

    public static readonly byte[] Data =
    {
%s
    };

    /// <summary>取字符字模(超出 ASCII 可打印范围回退为下划线)</summary>
    public static byte[] Glyph(char c)
    {
        int i = (int)c;
        if (i < 32 || i > 126) i = (int)'_';
        byte[] g = new byte[CharW];
        Array.Copy(Data, (i - 32) * CharW, g, 0, CharW);
        return g;
    }
}
""" % body

with open("fontdata.cs", "w", encoding="utf-8") as f:
    f.write(cs)
print("fontdata.cs generated,", len(data), "bytes")
