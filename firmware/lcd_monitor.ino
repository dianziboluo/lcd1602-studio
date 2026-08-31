/*
  电脑状态监视器 - ESP32-C3 端固件 (合宙 AirM2M CORE ESP32C3)  v3 所见即所得

  主机端 (LCD1602 Studio) 通过 USB 串口每 1 秒下发渲染好的两行文本:
      L0,<滚动0/1>,<文本>\n       例: L0,0,CPU 32% SOC 45C
      L1,<滚动0/1>,<文本>\n            L1,1,别卷了 风扇 3400RPM
  文本最大 16 列, 超出且滚动=1 时走马灯; 文本可含逗号。

  兼容旧协议 (monitor.exe): C:<cpu占用>;S:<soc温度>;F:<风扇>;M:<内存>\n
      收到后会按 行1 CPU xx% SOC xxC / 行2 FAN xxxx RAM xx% 排版显示。

  接线 (转接板 4 线连到开发板):
     转接板 VCC -> 3.3V      (整机统一 3.3V 供电)
     转接板 GND -> GND
     转接板 SDA -> GPIO4     (I2C_SDA)
     转接板 SCL -> GPIO5     (I2C_SCL)
  屏亮但没字: 调转接板上的蓝色电位器(对比度)。

  Arduino IDE 工具菜单:
     开发板          -> esp32 -> AirM2M CORE ESP32C3
     USB CDC On Boot -> Enabled      (必须,否则 Serial 不工作)
*/

#include <Wire.h>
#include <LiquidCrystal_I2C.h>

const int I2C_SDA = 4;
const int I2C_SCL = 5;
const uint8_t LCD_ADDRS[] = {0x27, 0x3F};   // 转接板常见地址,自动探测
const unsigned long NO_DATA_MS = 5000;

LiquidCrystal_I2C* lcd = NULL;
uint8_t lcdAddr = 0;

// CGRAM 槽1: ℃ 自定义字形(与 GUI 模拟器同款; ° 用 ROM 的 0xDF)
// 注意: HD44780 惯例是每行字节 高位(bit4)=最左列, 低位(bit0)=最右列
const byte CGRAM_CELSIUS[8] = {0x0E, 0x0A, 0x0E, 0x0C, 0x02, 0x02, 0x0C, 0x00};

// 把 GUI 侧字节(bit0=最左) 转成 LCD 侧字节(bit4=最左)
byte bitRev5(byte b) {
  return (byte)(((b & 0x01) << 4) | ((b & 0x02) << 2) | (b & 0x04)
              | ((b & 0x08) >> 2) | ((b & 0x10) >> 4));
}

String  line[2] = {"", ""};
bool    scrollF[2] = {false, false};
int     off[2] = {0, 0};
String  lastDraw[2] = {"", ""};
unsigned long lastDataMs = 0;
bool noDataShown = false;
static String lineBuf;

// 旧协议兼容
int oldCpu = -1, oldSoc = -1, oldFan = -1, oldRam = -1;

bool scanLcd() {
  for (int i = 0; i < 2; i++) {
    Wire.beginTransmission(LCD_ADDRS[i]);
    if (Wire.endTransmission() == 0) { lcdAddr = LCD_ADDRS[i]; return true; }
  }
  return false;
}

void setup() {
  Serial.begin(115200);
  Wire.begin(I2C_SDA, I2C_SCL);   // 必须主动指定引脚(ESP32-C3 默认是 8/9)
  delay(50);

  Serial.println("\n[monitor] boot, scanning I2C...");
  while (!scanLcd()) {
    Serial.println("LCD not found, retry in 2s (check wiring)");
    delay(2000);
  }
  Serial.printf("LCD found @ 0x%02X\n", lcdAddr);

  lcd = new LiquidCrystal_I2C(lcdAddr, 16, 2);
  // 调用 begin() 而非 init() —— init() 内部会 Wire.begin() 把引脚重置回默认
  lcd->begin(16, 2);
  uint8_t empty[8] = {0, 0, 0, 0, 0, 0, 0, 0};
  for (int s = 0; s < 8; s++) {
    uint8_t d[8];
    for (int i = 0; i < 8; i++)
      d[i] = s == 1 ? bitRev5(CGRAM_CELSIUS[i]) : empty[i];
    lcd->createChar(s, d);   // 槽1 默认 ℃, 其余空白
  }
  lcd->backlight();
  lcd->setCursor(0, 0);
  lcd->print("Wait for PC data ");
}

// 用宽 16 的字段绘制一行(补空格清残字)
void drawRow(int r) {
  char out[17];
  int len = line[r].length();
  bool marq = scrollF[r] && len > 16;
  for (int i = 0; i < 16; i++) {
    int p = marq ? off[r] + i : i;
    char c = (p < len) ? line[r].charAt(p) : ' ';
    unsigned char code = (unsigned char)c;
    if (code >= 1 && code <= 8) code = code - 1;   // 协议标记 {g1}~{g8} -> CGRAM 槽0..7
    out[i] = (char)code;
  }
  out[16] = 0;
  if (lastDraw[r] == out) return;
  lastDraw[r] = out;
  lcd->setCursor(0, r);
  lcd->print(out);
}

// 走马灯推进
void animScroll() {
  unsigned long now = millis();
  static unsigned long last = 0;
  if (now - last < 240) return;
  last = now;
  for (int r = 0; r < 2; r++) {
    if (scrollF[r] && line[r].length() > 16) {
      off[r]++;
      if (off[r] > line[r].length()) off[r] = 0;
      drawRow(r);
    }
  }
}

void handleLine(const String& s) {
  // 字形下发: CG,<槽0-7>,<8行字节>
  if (s.startsWith("CG,")) {
    int slot, b0, b1, b2, b3, b4, b5, b6, b7;
    if (sscanf(s.c_str(), "CG,%d,%d,%d,%d,%d,%d,%d,%d,%d",
               &slot, &b0, &b1, &b2, &b3, &b4, &b5, &b6, &b7) == 9
        && slot >= 0 && slot < 8) {
      uint8_t d[8] = {(uint8_t)b0, (uint8_t)b1, (uint8_t)b2, (uint8_t)b3,
                      (uint8_t)b4, (uint8_t)b5, (uint8_t)b6, (uint8_t)b7};
      for (int i = 0; i < 8; i++) d[i] = bitRev5(d[i]);   // GUI 位序 -> LCD 位序(防镜像)
      lcd->createChar(slot, d);
      // 若有行正在用该字形, 重画一次刷新
      lastDraw[0] = ""; lastDraw[1] = "";
      drawRow(0); drawRow(1);
    }
    return;
  }
  // 新协议: L0,<扫>,<文本>  或  L1,<扫>,<文本>
  if (s.length() > 3 && (s.charAt(0) == 'L' || s.charAt(0) == 'l')
      && (s.charAt(1) == '0' || s.charAt(1) == '1') && s.charAt(2) == ',') {
    int r = s.charAt(1) - '0';
    int comma = s.indexOf(',', 3);
    int sc = 0;
    if (comma > 3) {
      String scs = s.substring(3, comma);
      sc = scs.toInt();
      line[r] = s.substring(comma + 1);
    } else {
      line[r] = s.substring(3);
    }
    if (line[r].length() > 48) line[r] = line[r].substring(0, 48);
    scrollF[r] = sc != 0;
    off[r] = 0;
    lastDraw[r] = "";
    lastDataMs = millis();
    noDataShown = false;
    drawRow(r);
    return;
  }
  // 旧协议兼容: C:<cpu>;S:<soc>;F:<fan>;M:<mem>
  int l1, t1, f1, m1;
  if (sscanf(s.c_str(), "C:%d;S:%d;F:%d;M:%d", &l1, &t1, &f1, &m1) == 4) {
    char a[17], b[17];
    if (l1 >= 0 && t1 >= 0) snprintf(a, 17, "CPU %2d%% SOC %dC", l1, t1);
    else if (l1 >= 0) snprintf(a, 17, "CPU %2d%% SOC --C", l1);
    else snprintf(a, 17, "CPU --%% SOC --C");
    if (f1 >= 0 && m1 >= 0) snprintf(b, 17, "FAN %4d RAM %2d%%", f1, m1);
    else if (f1 >= 0) snprintf(b, 17, "FAN %4d RAM --%%", f1);
    else if (m1 >= 0) snprintf(b, 17, "FAN ---- RAM %2d%%", m1);
    else snprintf(b, 17, "FAN ---- RAM --%%");
    line[0] = a; line[1] = b; scrollF[0] = scrollF[1] = false;
    off[0] = off[1] = 0; lastDraw[0] = lastDraw[1] = "";
    lastDataMs = millis(); noDataShown = false;
    drawRow(0); drawRow(1);
  }
}

void loop() {
  // 心跳: 每 5 秒打印当前行, 方便从 PC 端确认固件在跑
  static unsigned long lastBeat = 0;
  if (millis() - lastBeat > 5000) {
    lastBeat = millis();
    Serial.print("[alive] l0=["); Serial.print(line[0]);
    Serial.print("] l1=["); Serial.print(line[1]); Serial.println("]");
  }

  animScroll();

  while (Serial.available()) {
    char c = (char)Serial.read();
    if (c == '\n') {
      if (lineBuf.length() > 0) handleLine(lineBuf);
      lineBuf = "";
    } else if (c != '\r') {
      lineBuf += c;
      if (lineBuf.length() > 96) lineBuf = "";
    }
  }
  // PC 端断开/退出时提示一次
  if (lastDataMs && millis() - lastDataMs > NO_DATA_MS && !noDataShown) {
    noDataShown = true;
    line[0] = "PC NO DATA..."; line[1] = "sync from Studio";
    scrollF[0] = scrollF[1] = false; off[0] = off[1] = 0;
    lastDraw[0] = lastDraw[1] = "";
    drawRow(0); drawRow(1);
  }
}
