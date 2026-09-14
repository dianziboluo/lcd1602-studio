/*
  LcdParallel.h —— 极简 HD44780 4 位并行驱动 (自包含, 不需要安装任何库)
  仅当 lcd_monitor.ino 里 LCD_DRIVER = LCD_DRIVER_PARALLEL 时被使用。

  接口与 LiquidCrystal_I2C 保持一致(begin/backlight/createChar/setCursor/print),
  因此主程序两种驱动共用同一份绘制代码。

  接线 (RW 直接接地, 只写不问忙):
      LCD 1  VSS -> GND
      LCD 2  VDD -> 3.3V
      LCD 3  V0  -> 10k 电位器中间脚 (另两脚接 3.3V / GND)
      LCD 4  RS  -> PIN_RS
      LCD 5  RW  -> GND
      LCD 6  E   -> PIN_EN
      LCD 7~10 D0~D3 -> 不接
      LCD 11 D4  -> PIN_D4
      LCD 12 D5  -> PIN_D5
      LCD 13 D6  -> PIN_D6
      LCD 14 D7  -> PIN_D7
      LCD 15 A   -> 3.3V 串 100~220Ω      LCD 16 K -> GND
*/
#ifndef LCD_PARALLEL_H
#define LCD_PARALLEL_H

#include <Arduino.h>

// ---------------- 引脚定义 (按实际接线修改) ----------------
#define PIN_RS  5    // LCD 4  RS
#define PIN_EN  4    // LCD 6  E
#define PIN_D4  6    // LCD 11 D4
#define PIN_D5  7    // LCD 12 D5
#define PIN_D6 10    // LCD 13 D6
#define PIN_D7  3    // LCD 14 D7

class LcdParallel {
 public:
  LcdParallel(int rs, int en, int d4, int d5, int d6, int d7) {
    _rs = rs; _en = en;
    _d[0] = d4; _d[1] = d5; _d[2] = d6; _d[3] = d7;
  }

  // 兼容 LiquidCrystal 的调用形式: begin(16, 2)
  void begin(int cols = 16, int rows = 2) {
    pinMode(_rs, OUTPUT);
    pinMode(_en, OUTPUT);
    for (int i = 0; i < 4; i++) { pinMode(_d[i], OUTPUT); digitalWrite(_d[i], LOW); }
    digitalWrite(_rs, LOW);
    digitalWrite(_en, LOW);
    delay(50);              // 上电等待 > 40ms
    nibble(0x3); delay(5);  // 8 位软复位序列 (HD44780 数据手册 Figure 24)
    nibble(0x3); delay(5);
    nibble(0x3); delay(1);
    nibble(0x2); delay(1);  // 切到 4 位模式
    cmd(0x28);              // 4 位 / 2 行 / 5x7 点阵
    cmd(0x0C);              // 开显示, 关光标
    cmd(0x06);              // 写入后地址自增
    cmd(0x01);              // 清屏
  }

  void backlight() {}       // 并口背光由硬件直接供电, 空操作

  void createChar(uint8_t n, uint8_t* d) {
    cmd((uint8_t)(0x40 | (uint8_t)(n << 3)));
    for (int i = 0; i < 8; i++) data(d[i]);
  }

  void setCursor(uint8_t c, uint8_t r) {
    cmd((uint8_t)((r == 0 ? 0x80 : 0xC0) + c));
  }

  void print(const char* s) { while (*s) data((uint8_t)*s++); }

 private:
  int _rs, _en, _d[4];

  // 送 4 位到 D4~D7 并打一个使能脉冲
  void nibble(uint8_t v) {
    for (int i = 0; i < 4; i++) digitalWrite(_d[i], (v >> i) & 1);
    digitalWrite(_en, HIGH); delayMicroseconds(2);
    digitalWrite(_en, LOW);  delayMicroseconds(40);
  }

  void send(uint8_t v, bool isData) {
    digitalWrite(_rs, isData ? HIGH : LOW);
    nibble((uint8_t)(v >> 4));      // 高 4 位
    nibble((uint8_t)(v & 0x0F));    // 低 4 位
  }

  void cmd(uint8_t v) {
    send(v, false);
    if (v <= 0x03) delay(2);                     // 清屏/回原点需要 1.5ms 以上
    else delayMicroseconds(60);
  }

  void data(uint8_t v) { send(v, true); delayMicroseconds(60); }
};

#endif  // LCD_PARALLEL_H
