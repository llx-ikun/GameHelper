# 游戏助手 (Game Helper)

一个面向 Windows 的本地游戏性能工具。最初为《永劫无间》(Naraka: Bladepoint) 设计，现已支持监控其他游戏。

A local Windows game-performance tool. Originally built for *Naraka: Bladepoint*, now supports monitoring other games too.

[English](#english) · [功能](#功能) · [构建](#构建) · [原理与边界](#原理与边界) · [License](#license)

---

## 功能

| 功能 | 说明 |
|---|---|
| **显存看护** | 实时显示显存占用、温度、GPU 占用与频率；峰值、越线时刻自动记录 |
| **真实帧率** | 通过 [PresentMon](https://github.com/GameTechDev/PresentMon) 读取真实 FPS 与 1% Low（需管理员） |
| **逐局分析** | 自动切分对局，输出每局帧率/1% Low/显存/温度对比 + 局内四等分曲线 |
| **自动判定** | 6 条数据驱动判定（热降频 / 显存耗尽 / 显存累积 / 持续变卡 / 偶发长帧 / 瓶颈在显卡），每条附实测依据 |
| **显存将满提醒** | 可选开关；带迟滞 + 冷却 + 静默，防止刷屏 |
| **一键腾显存** | 回收**其他进程**占用的显存（硬保护名单，绝不碰系统/安全软件/游戏自身） |
| **监控其他游戏** | 从进程列表切换监控目标；非永劫无间目标只报观测值、不下结论（见下文） |

> **重要设计决定**：6 条判定与针对性建议的阈值，是在一份《永劫无间》99 分钟实战日志上标定出来的。
> 监控其他游戏时，工具**只输出帧率 / 1% Low / 显存 / 温度 / 占用 / 频率这些纯观测值**，
> 自动判定、对局拆分与建议整体关闭——换一个游戏套用同样的阈值就是假结论。
> 详见 [DESIGN.md §4.10](DESIGN.md)。

---

## 下载

去 [Releases](../../releases) 下载 `游戏助手.zip`，解压即用（自包含，无需安装 .NET）。

- **需要 NVIDIA 显卡**：显卡数据来自 `nvidia-smi`。
- **帧率采集需要管理员**：PresentMon 启动内核 ETW 会话需要管理员权限，
  否则帧率会显示「需管理员」，其余功能不受影响。

---

## 构建

前置：`.NET SDK 9.0` 或更高。

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

产物在 `dist\游戏助手.exe`（自包含单文件，约 46 MB）。

| 参数 | 作用 |
|---|---|
| `-Run` | 构建完顺便启动 |
| `-SkipPublish` | 只编译，不发布单文件 |
| `-Shots` | 额外生成界面截图 |

自检（无界面验证）：

```powershell
.\dist\游戏助手.exe --selftest
```

---

## 原理与边界

本工具是**只读观测 + 进程管理**，不注入、不 hook、不改游戏内存：

- **显卡数据** → `nvidia-smi`（随驱动附带）
- **进程显存** → PDH 计数器 `\GPU Process Memory`（P/Invoke `pdh.dll`，零依赖）
- **物理内存** → `GlobalMemoryStatusEx`
- **真实帧率** → PresentMon 监听显卡 Present 事件（只读 ETW，不注入）

### 反作弊

对《永劫无间》的 Neac 反作弊，实测无害（以管理员身份运行 99 分钟无异常）。

**但换游戏不能想当然**：部分反作弊（如《无畏契约》的 Vanguard）会检测系统上
是否存在 ETW 跟踪会话，即使工具没有碰它的进程。请在目标游戏上先小规模验证。

### 明确不做（且不打算做）

- ❌ 注入游戏 / hook Present
- ❌ 加载内核驱动
- ❌ `EmptyWorkingSet` 类"内存清理"（对显存零效果，做了就是骗人）
- ❌ 自动提权

更完整的设计权衡见 [DESIGN.md](DESIGN.md)（含每个阈值的标定过程与数据）。

---

## 依赖

- [PresentMon](https://github.com/GameTechDev/PresentMon)（Intel，MIT License）
  —— 内嵌于构建产物，见 `tools/PresentMon-LICENSE.txt`

---

## License

本项目采用 [MIT License](LICENSE)。PresentMon 为其自身的 MIT License。

> 注意：`LICENSE` 中的版权归属当前为占位文字，发布前请替换为你自己的名字/组织名。

---

## English

A local Windows tool for monitoring GPU VRAM, temperature, and real frame rate
during gameplay. Originally built for *Naraka: Bladepoint*; can also monitor other
games (observation-only mode for non-Naraka targets).

- Requires an NVIDIA GPU (uses `nvidia-smi`).
- Real frame rate requires running as administrator (PresentMon).
- Read-only observation + process management. No injection, no hooking.

Licensed under MIT. See [DESIGN.md](DESIGN.md) for the full design rationale
(including how each threshold was calibrated).
