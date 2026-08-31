# LCD1602 Studio

桌面端 **1602 液晶屏工作台** —— 在电脑上模拟一块真实的 HD44780 1602 屏(5x7 点阵、蓝/绿背光、走马灯),
编辑两行内容(自由文本 + 实时变量),**所见即所得地同步到 ESP32-C3 + LCD1602 真屏**。

配套硬件:合宙 CORE ESP32-C3 + PCF8574 I2C 转接板 + LCD1602。

```
PC 端 LCD1602Studio.exe (C#/WinForms, 零安装, 系统自带 csc 编译)
   ├── 数据引擎: LibreHardwareMonitorLib (CPU/SoC/内存/占用, MPL-2.0)
   ├── 可选 HWiNFO 共享内存后端 (笔记本风扇/真实 CPU 温度等 282+ 项)
   └── USB 串口 115200 → ESP32-C3 (firmware/lcd_monitor.ino)
                              └── I2C (GPIO4=SDA, GPIO5=SCL) → PCF8574 → LCD1602
```

## 特性

- **1602 模拟器**:自绘 5x7 点阵(非普通字体),蓝/绿/灭三档背光,未点亮像素可见,超长行走马灯
- **所见即所得同步**:「同步到真屏」开关,自动找 ESP32 串口,每秒下发渲染好的两行文本
- **点阵字形编辑器**:手画 5x7 自定义字符(8 个 CGRAM 槽位),缩略图列表,**拖拽到模拟屏任意格**自动插入 `{g1}`~`{g8}` 占位符;真屏同步 CGRAM(含 ℃、★、♥ 等)
- **数据台**:搜索 + [全部|内置|HWiNFO] 分段 + **分组折叠**(常用/HWiNFO·温度/HWiNFO·风扇/HWiNFO·占用/字形),类型徽章(内/温/扇/占),右对齐实时值,可一键收起
- **命令面板**:行内「插入变量」→ ⌘K 式搜索面板,Enter 插入 / Esc 关闭
- **变量插槽**:`{cpu}` `{soc}` `{gpu}` `{ram}` `{fan}` `{temp}` + HWiNFO 任意项;数值两位数(06)防跳动;`--` 表示读不到
- **预设管理**:整套模板(两行+滚动+背光)命名保存/载入/删除
- **零安装**:Windows 自带 .NET Framework 编译器(csc)构建,无 SDK、无运行时安装

> 屏幕效果(真实截图):

> ![sim](docs/selftest_ui.png)

## 快速开始(PC 端)

```
Windows 10/11
1. git clone 本仓库并进入 studio/
2. 双击 build.bat            → 首次自动下载 LibreHardwareMonitorLib(MPL-2.0)
3. 运行 LCD1602Studio.exe    → 开始编辑你的"屏幕"
```

可选数据源:状态条点「HWiNFO · 点击安装引导」,安装 HWiNFO64 并启用 Shared Memory Support
(本工具只读其公开共享内存接口,不捆绑任何 HWiNFO 组件,详见 THIRD_PARTY_LICENSES.md)。

## 接线(真屏)

| 转接板 | ESP32-C3 | 针脚 |
|---|---|---|
| VCC | 3.3V | 第 26 脚(或 18) |
| GND | GND | 第 25 脚(或 17) |
| SDA | GPIO4 (I2C_SDA) | 第 28 脚 |
| SCL | GPIO5 (I2C_SCL) | 第 27 脚 |

> 整机统一 3.3V 供电;屏亮但无字 → 调转接板蓝色电位器(对比度)。

## 固件烧录(Arduino IDE)

1. `firmware/lcd_monitor.ino`;库:`LiquidCrystal_I2C`(YwRobot/PCF8574 版)
2. 工具菜单:开发板 `esp32 → AirM2M CORE ESP32C3`;**USB CDC On Boot = Enabled**
3. 上传(此板自动下载电路不可靠时:按住板载 BOOT 键再点上传)

## 数据协议 (USB 串口 115200)

```
L0,<滚动0/1>,<文本>\n        # 行1(文本可含逗号, 16 列内显示, 滚动=走马灯)
L1,<滚动0/1>,<文本>\n        # 行2
CG,<槽0-7>,<8行字节,逗号分隔>\n  # 自定义字形(CGRAM)
兼容旧格式: C:<cpu>;S:<soc>;F:<fan>;M:<mem>\n
特殊字节: 0x01..0x08 → CGRAM 槽0..7; 0xDF = °(ROM 度符号)
```

## 目录结构

```
lcd1602-studio/
├── studio/      桌面 GUI(LCD1602Studio): 源码 + build.bat(自动下载 LHM)
├── firmware/    ESP32-C3 固件(lcd_monitor.ino)
├── monitor/     旧版命令行主机(monitor.cs, 协议兼容, 可继续用于固定排版)
├── docs/        截图
├── LICENSE              MIT
└── THIRD_PARTY_LICENSES.md
```

## 已知限制

- 笔记本模态:AMD 移动 CPU (8845HS 等) 的 Tctl 温度 LibreHardwareMonitor 读不到(开源社区已知问题),
  本工具以 SoC 核显温度代之;HWiNFO 后端可用时 `{temp}` 为真实 Tctl/Tdie。
- 此开发板/笔记本的风扇转速:LHM 与 HWiNFO 均未暴露 EC 传感器(实测 282 项读数中风扇 0 项),
  `{fan}` 显示 `--` 属硬件限制。
- 中文等非 ASCII 字符在 LCD 上显示为下划线(HD44780 物理限制);1602 屏幕 ASCII 长度 16 列/行。

## 许可证

本项目代码 **MIT**;第三方组件详见 [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md)。
