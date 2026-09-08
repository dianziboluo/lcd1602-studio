# 扩展模块 (PC 端)

> **一句话:显示器只负责显示,一切功能都在 PC 端的模块里。**
> 新增一个数据源 = 写一个模块 + 注册一行;固件(ESP32)完全不用动。

---

## 1. 架构

```
PC 端(LCD1602Studio.exe)
├── 数据引擎: LHM / HWiNFO / 系统计数器        ┐
├── 扩展模块(本文件):                          ├─→ 统一变量 → 模板渲染 → 两行文本
│     ├── dsh       DSH 运行/空闲状态          │            │
│     └── deepseek  余额 + 平价时段倒计时       ┘            │
└───────────────────────────────────────────────────────────┤
                                                            ▼
                              USB 串口 L0/L1 → ESP32-C3(只画字符)→ LCD1602
```

模块在**后台线程**按各自间隔轮询;取到的值变成模板变量,渲染成两行文本后经串口发给真屏。
**模块异常只影响它自己**(显示 `--` + 状态条黄色徽章),不会影响主程序或其它模块。

---

## 2. 内置模块一览

| 模块 | 分组名 | 轮询间隔 | 变量 |
|---|---|---|---|
| DSH | `DSH` | 3 秒 | `{dsh.state}` `{dsh.min}` |
| DeepSeek | `DeepSeek` | 5 分钟(余额) / 实时(倒计时) | `{ds.bal}` `{ds.bal.grant}` `{ds.bal.top}` `{ds.bal.age}` `{ds.peak.now}` `{ds.rate}` `{ds.peak.in}` `{ds.peak.left}` |

在「数据台」里能看到这两个分组(徽章 + 实时值),点任意一行即把变量插入当前编辑行;
也可以直接在模板里手写 `{dsh.state}` 这样的占位符。

---

## 3. DSH 模块:运行 / 空闲

### 变量

| 变量 | 含义 | 取值示例 |
|---|---|---|
| `{dsh.state}` | DSH 状态 | `运行中` / `空闲` / `未启动` |
| `{dsh.min}` | 距上次活动多少分钟 | `0` / `7` / `--` |

### 判定逻辑(三信号组合)

1. **会话文件活动**(主信号):`~/.dsh/sessions/**/session.jsonl.zstd` 中最新文件
   在 `idleSeconds`(默认 90 秒)内有写入 → `运行中`
2. **API 连接**(辅助):DSH 的 node 进程存在到 `:443` 的 ESTABLISHED 连接 →
   `运行中`(覆盖“只生成、暂时不写文件”的阶段)
3. 进程在但两条都不满足 → `空闲`
4. node 进程与 3080 端口都不在、且长时间无活动 → `未启动`

> 说明:会话文件是 zstd 压缩的,**我们只读它的修改时间,不解析内容** —— 这样 DSH 内部
> 格式变化也不会影响本模块。

### 配置(modules.ini `[dsh]`)

| 键 | 默认 | 说明 |
|---|---|---|
| `dshHome` | `%USERPROFILE%\.dsh` | DSH 家目录 |
| `idleSeconds` | `90` | 多久无写入算空闲 |

---

## 4. DeepSeek 模块:余额 + 平价倒计时

### 变量

| 变量 | 含义 | 取值示例 |
|---|---|---|
| `{ds.bal}` | 账户总余额 | `8.89` |
| `{ds.bal.grant}` | 赠金余额 | `0.00` |
| `{ds.bal.top}` | 充值余额 | `8.89` |
| `{ds.bal.age}` | 余额数据是几分钟前的 | `0` / `12514` |
| `{ds.peak.now}` | 当前时段 | `空闲` / `高峰` |
| `{ds.rate}` | 当前价率 | `五折` / `全价` |
| `{ds.peak.in}` | 距下一个空闲时段开始 | `2h15m`(已在空闲中则 `0m`) |
| `{ds.peak.left}` | 空闲还剩多久(高峰中为 `--`) | `14h23m` |

### 余额取值顺序(两路,自动)

1. **读 DSH 缓存**(零成本、无需密钥):
   `~/.dsh/dsh-usage/provider-snapshots.json` → `providers["deepseek-official"].balance.totalBalance`
2. **缓存过期**(超过 `cacheMaxAgeMin`,默认 30 分钟)→ 调用官方接口
   `GET https://api.deepseek.com/user/balance`(Bearer)刷新,
   同时得到赠金 / 充值余额
3. 两路都拿不到 → 显示 `--`,状态条徽章变黄,**不崩**

### 平价(空闲)时段规则

官方现行规则(**可在配置里改**):

> 高峰时段 = 北京时间 **周一至周五 09:00–12:00 与 14:00–18:00**,其余时间均为空闲时段,
> 空闲时段价格是高峰的一半。

倒计时**纯本地计算**,不联网:
- 高峰中:`{ds.peak.in}` = 距本段高峰结束(即空闲开始)
- 空闲中:`{ds.peak.left}` = 距下一个高峰开始
- 跨天、跨周末都自动处理

### 配置(modules.ini `[deepseek]`)

| 键 | 默认 | 说明 |
|---|---|---|
| `snapshotsPath` | `~/.dsh/dsh-usage/provider-snapshots.json` | DSH 缓存的 provider 快照 |
| `credentialsPath` | `~/.dsh/.credentials.yaml` | 从中读取 `DEEPSEEK_API_KEY` |
| `apiKey` | 空 | 手填 API Key(优先于凭据文件) |
| `cacheMaxAgeMin` | `30` | 缓存多久算过期 |
| `peak` | `1-5 09:00-12:00;1-5 14:00-18:00` | 高峰时段,`星期范围 起-止`,分号分隔,`1`=周一 `7`=周日 |

---

## 5. 配置文件

位置:`%APPDATA%\LCD1602Studio\modules.ini`(首次运行自动生成带注释的示例,**改完重启生效**)

```ini
[dsh]
# dshHome=
idleSeconds=90

[deepseek]
# snapshotsPath=
# credentialsPath=
# apiKey=
cacheMaxAgeMin=30
peak=1-5 09:00-12:00;1-5 14:00-18:00
```

> `#` 或 `;` 开头为注释;`键=值` 不区分大小写。

---

## 6. 隐私与安全

- API Key **只在本地读取**(`~/.dsh/.credentials.yaml` 或你在配置里手填),
  **不写日志、不上传、不进仓库**
- 模块只读 DSH 自己的会话/缓存文件,不修改它们
- 余额查询是官方接口的只读 GET,不产生费用

---

## 7. 自己加一个模块(三步)

1. 在 `studio/Modules/` 新建 `XxxModule.cs`,继承 `ModuleBase`:

```csharp
internal class XxxModule : ModuleBase
{
    public override string Id   { get { return "xxx"; } }      // modules.ini 的节名
    public override string Name { get { return "XXX"; } }      // 数据台分组名
    public override int PollMs  { get { return 5000; } }       // 轮询间隔

    protected override void DoPoll() { /* 采集数据, 允许阻塞; 抛异常会被隔离 */ }

    public override IEnumerable<ModuleVar> Variables()
    {
        return new List<ModuleVar> { Make("xxx.value", "我的数值", "", delegate { return "42"; }) };
    }
}
```

2. 在 `MainForm` 构造函数里注册一行:

```csharp
_modules.Add(new XxxModule());
```

3. 在 `build.bat` 的文件列表里加上 `Modules\XxxModule.cs`,重新编译。
   → 数据台自动多出「XXX」分组,模板里 `{xxx.value}` 立即可用,**固件不用动**。

---

## 8. 故障排查

| 现象 | 原因 / 处理 |
|---|---|
| `{dsh.state}` 一直 `空闲` | 阈值太短(调大 `idleSeconds`),或 DSH 正在纯生成(等下一次工具调用) |
| `{dsh.state}` = `未启动` | DSH 没开,或 `dshHome` 配错 |
| `{ds.bal}` = `--` | 缓存文件不存在且 API 失败(检查 `apiKey` / 网络 / Key 是否有效) |
| `{ds.bal.age}` 很大 | 缓存过期但 API 刷新失败;界面显示的是缓存值,仅供参考 |
| 状态条模块徽章变黄 | 悬停可看错误摘要;多为文件被移动或权限问题 |
| 全部正常但屏上没变化 | 检查模板是否用了 `{变量}`、是否点了「同步到真屏」 |
