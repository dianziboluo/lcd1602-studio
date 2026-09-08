# Extension modules (PC side)

> **One sentence: the display only displays; every feature lives in a PC-side module.**
> Adding a data source = write a module + register one line. The ESP32 firmware never changes.

---

## 1. Architecture

```
PC (LCD1602Studio.exe)
├── Data engine: LHM / HWiNFO / system counters   ┐
├── Extension modules (this document):             ├─→ unified variables → template → two lines
│     ├── dsh       DSH busy/idle state           │            │
│     └── deepseek  balance + off-peak countdown  ┘            │
└─────────────────────────────────────────────────────────────┤
                                                              ▼
                        USB serial L0/L1 → ESP32-C3 (draws text) → LCD1602
```

Modules poll on a background thread at their own interval; values become template variables,
get rendered into two lines and pushed to the real LCD. A module failure only affects itself
(shows `--` + an amber badge) — never the app or other modules.

---

## 2. Built-in modules

| Module | Deck group | Interval | Variables |
|---|---|---|---|
| DSH | `DSH` | 3 s | `{dsh.state}` `{dsh.min}` |
| DeepSeek | `DeepSeek` | 5 min (balance) / live (countdown) | `{ds.bal}` `{ds.bal.grant}` `{ds.bal.top}` `{ds.bal.age}` `{ds.peak.now}` `{ds.rate}` `{ds.peak.in}` `{ds.peak.left}` |

They appear as groups in the data deck (badge + live value). Click a row to insert the variable
into the focused line, or just type `{dsh.state}` in the template.

---

## 3. DSH module — busy / idle

| Variable | Meaning | Example |
|---|---|---|
| `{dsh.state}` | DSH state | `运行中` / `空闲` / `未启动` |
| `{dsh.min}` | minutes since last activity | `0` / `7` / `--` |

**Detection (three signals):**
1. **Session activity (primary)**: the newest `~/.dsh/sessions/**/session.jsonl.zstd` was written
   within `idleSeconds` (default 90) → `运行中`
2. **API connection (secondary)**: DSH's node process has an ESTABLISHED connection to `:443`
   → `运行中` (covers long silent generation)
3. Process alive but neither of the above → `空闲`
4. No node process, no port 3080, no recent activity → `未启动`

> The session files are zstd-compressed — **we only read their modification time, never the
> contents**, so internal format changes in DSH can't break this module.

Config (`[dsh]`): `dshHome` (default `%USERPROFILE%\.dsh`), `idleSeconds` (default `90`).

---

## 4. DeepSeek module — balance + off-peak countdown

| Variable | Meaning | Example |
|---|---|---|
| `{ds.bal}` | total balance | `8.89` |
| `{ds.bal.grant}` | granted balance | `0.00` |
| `{ds.bal.top}` | topped-up balance | `8.89` |
| `{ds.bal.age}` | age of the balance data (minutes) | `0` / `12514` |
| `{ds.peak.now}` | current pricing period | `空闲` / `高峰` |
| `{ds.rate}` | current rate | `五折` / `全价` |
| `{ds.peak.in}` | time until next off-peak period starts | `2h15m` (`0m` when already off-peak) |
| `{ds.peak.left}` | time left in the current off-peak period (`--` during peak) | `14h23m` |

**Balance lookup (two paths, automatic):**
1. **Read DSH's cache** (free, no key): `~/.dsh/dsh-usage/provider-snapshots.json`
   → `providers["deepseek-official"].balance.totalBalance`
2. **Cache stale** (older than `cacheMaxAgeMin`, default 30 min) → call the official
   `GET https://api.deepseek.com/user/balance` (Bearer) which also returns granted/topped-up balance
3. Both fail → show `--`, amber badge, **no crash**

**Off-peak rules** (official, configurable):

> Peak = **Mon–Fri 09:00–12:00 and 14:00–18:00 Beijing time**; everything else is off-peak
> and costs half the peak price.

The countdown is computed **locally** (no network), handles cross-day and weekends.

Config (`[deepseek]`): `snapshotsPath`, `credentialsPath`, `apiKey`, `cacheMaxAgeMin` (30),
`peak` (`1-5 09:00-12:00;1-5 14:00-18:00`, weekday ranges, `1`=Mon `7`=Sun).

---

## 5. Config file

`%APPDATA%\LCD1602Studio\modules.ini` — auto-created with comments on first run; restart to apply.

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

Lines starting with `#` or `;` are comments; keys are case-insensitive.

---

## 6. Privacy

- The API key is read **locally only** (from `~/.dsh/.credentials.yaml` or typed into the config);
  never logged, never uploaded, never committed
- Modules only read DSH's own session/cache files; they never modify them
- The balance query is a read-only official GET and costs nothing

---

## 7. Add your own module (three steps)

1. Create `studio/Modules/XxxModule.cs` extending `ModuleBase` (implement `Id`, `Name`, `PollMs`,
   `DoPoll`, `Variables`).
2. Register it in `MainForm`: `_modules.Add(new XxxModule());`
3. Add `Modules\XxxModule.cs` to the file list in `build.bat` and rebuild.
   → a new deck group appears automatically and `{xxx.value}` works in templates.
   **No firmware changes.**

---

## 8. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `{dsh.state}` stuck at `空闲` | raise `idleSeconds`, or DSH is in a long silent generation phase |
| `{dsh.state}` = `未启动` | DSH not running, or wrong `dshHome` |
| `{ds.bal}` = `--` | no cache file and API failed (check `apiKey` / network / key validity) |
| `{ds.bal.age}` very large | cache expired and refresh failed; the shown balance is the cached one |
| Module badge amber | hover for the error summary; usually a moved file or permissions |
| Everything fine but the LCD doesn't change | is the template using `{variables}` and is "Sync to screen" on? |
