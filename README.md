# STS2 MCP Bridge

STS2 MCP Bridge 把 Slay the Spire 2 `v0.107.1` 的单人游戏状态暴露为本机 MCP 工具，让 Codex 先读取结构化状态，再提交一个带精确 `state_version` 的合法动作。架构由两个进程组成：游戏内 Mod 只观察和执行动作；本地 .NET 控制台程序同时提供仅绑定 `127.0.0.1` 的 HTTP 邮箱和 MCP stdio 服务。它不包含 LLM 客户端，也不会把游戏数据发送到外部网络。主菜单提供受限的继续游戏、打开单人模式、标准模式和已解锁角色选择动作，因此 Codex 可以从主菜单开始一局。

这是社区实验项目，不是 Mega Crit 官方产品。Manifest 固定游戏版本 `0.107.1`；升级游戏后应重新验证 API 和行为。

## 前置条件

- macOS Apple Silicon 上的 Slay the Spire 2 `v0.107.1`，并已启用游戏的 C# Mod 加载方式。
- .NET 9 SDK。
- Codex CLI 或其他支持 MCP stdio 的客户端。

仓库不包含或重新分发游戏 DLL、反编译源码、存档或凭据。`Core` 和 `Server` 没有任何游戏专有引用。

## 构建、测试和安装

在仓库根目录运行：

```sh
chmod +x scripts/build-install.sh scripts/run-server.sh
./scripts/build-install.sh
```

脚本依次运行核心/协议测试、构建本地服务器、使用游戏 managed 目录构建 Mod，然后只安装下面两个文件：

```text
.../SlayTheSpire2.app/Contents/MacOS/mods/Sts2McpBridge/
  Sts2McpBridge.dll
  Sts2McpBridge.json
```

macOS Steam 默认路径已经内置在脚本中。自定义安装位置时使用：

```sh
STS2_MANAGED_DIR='/path/to/data_sts2_macos_arm64' \
STS2_MODS_DIR='/path/to/game/MacOS/mods' \
./scripts/build-install.sh
```

公开的 csproj 不包含用户路径。等价的精确手动命令是：

```sh
dotnet run --project tests/Sts2McpBridge.Tests.csproj -c Release
dotnet build Sts2McpBridge.Server.csproj -c Release
dotnet build Sts2McpBridge.Mod.csproj -c Release \
  -p:Sts2ManagedDir='/path/to/data_sts2_macos_arm64'
```

Mod 项目把 BCL-only Core 源码直接编入唯一的游戏 DLL。服务器通过项目引用使用 `Sts2McpBridge.Core.dll`，但服务器和 Core 都不会安装到游戏目录。

## 启动服务器

独立调试时先用守护模式启动服务器，再启动游戏：

```sh
./scripts/run-server.sh
```

也可以直接运行：

```sh
dotnet run --project /absolute/path/to/sts2-mcp-bridge/Sts2McpBridge.Server.csproj -- --daemon
```

配置项：

- `STS2_MCP_PORT`：HTTP 端口，默认 `37845`。服务器始终只监听 `127.0.0.1`。
- `STS2_MCP_TOKEN`：显式共享 token。token 不会写入日志或 MCP 工具输出。
- `STS2_MCP_TOKEN_FILE`：共享 token 文件；默认 `~/.config/sts2-mcp-bridge/token`。
- `STS2_MCP_URL`：只供 Mod 使用，默认 `http://127.0.0.1:37845`。非 loopback HTTP URL 会被拒绝。

没有显式 token 时，服务器生成 32 字节随机 token，并把文件权限设置为仅当前用户可读写（平台支持时为 `0600`）。Mod 从相同环境变量或默认文件读取 token，因此通常不需要把 token 放进命令行。若 Steam 启动的游戏使用不同环境，请给服务器和游戏设置相同的绝对 `STS2_MCP_TOKEN_FILE`。不要把 token 文件提交到仓库。

## Codex MCP 配置

在 `~/.codex/config.toml` 中加入下面配置，并把路径改成仓库的绝对路径：

```toml
[mcp_servers.sts2]
command = "dotnet"
args = ["run", "--project", "/absolute/path/to/sts2-mcp-bridge/Sts2McpBridge.Server.csproj", "--"]
env = { STS2_MCP_TOKEN_FILE = "/Users/your-name/.config/sts2-mcp-bridge/token", STS2_MCP_PORT = "37845" }
startup_timeout_sec = 20
tool_timeout_sec = 40
```

## 本地知识库

Bridge 仓库不包含第三方游戏数据。可按个人非商业用途下载 Spire Codex 的完整简体中文 JSON 快照：

```sh
chmod +x scripts/download-spire-codex-knowledge.sh
./scripts/download-spire-codex-knowledge.sh
```

默认目录：

```text
~/.local/share/sts2-knowledge/spire-codex/zhs/
```

数据源为 <https://spire-codex.com/api/exports/zhs>，上游仓库为 <https://github.com/ptrlrd/spire-codex>，许可为 PolyForm Noncommercial License 1.0.0。数据只保存在本机，不会被构建脚本安装或提交到本仓库。自定义目录时给 Codex MCP 配置增加：

```toml
env = {
  STS2_MCP_TOKEN_FILE = "/Users/your-name/.config/sts2-mcp-bridge/token",
  STS2_MCP_PORT = "37845",
  STS2_KNOWLEDGE_DIR = "/absolute/path/to/spire-codex/zhs"
}
```

Knowledge MCP 工具：

- `sts2_knowledge_manifest`：来源、许可证、覆盖数量和本地路径。
- `sts2_knowledge_lookup`：按类型和 ID 精确查询卡牌、遗物、药水、怪物、事件、遭遇、能力等。
- `sts2_knowledge_search`：按中英文文本或 ID 搜索。
- `sts2_knowledge_relevant`：批量查询当前状态中的实体 ID。

Codex 遇到新怪物、事件、卡牌、遗物或药水时应先查询相关知识。静态知识可能存在解析缺口；返回的 `completeness` 会标记不完整怪物状态机，实时游戏状态和 `NextMove` 永远优先。

Codex MCP 配置不要加 `--daemon`；Codex 通过 stdio 持有 Server 生命周期。第一次使用工具时会创建 token 文件。随后启动游戏即可；Codex 可以通过合法主菜单动作继续旧 run，或依次打开 `Single Player`、标准模式并选择角色。不要同时手动启动第二个占用相同端口的服务器。

可以直接对 Codex 下达：

```text
使用 sts2 MCP 工具开始一局 A10 铁甲战士。持续读取状态，只执行当前 legal_actions 中带精确 state_version 的动作；每次执行后等待状态变化。以保存生命、到达并击败本幕 Boss 为目标，直到 run 结束或我要求暂停。
```

## MCP 工具

- `sts2_get_state`：读取最新完整观察、暂停状态和合法动作。
- `sts2_get_legal_actions`：只读取当前版本和合法动作。
- `sts2_execute_action`：提交 `{state_version, action_id}`。两者都必须来自同一次观察。
- `sts2_wait_for_state_change`：等待版本变化，最长 30 秒。
- `sts2_pause`：暂停并取消尚未被 Mod 领取的动作。
- `sts2_resume`：恢复动作领取。
- `sts2_get_history`：读取最近的排队、执行、拒绝和取消记录。
- `sts2_knowledge_manifest` / `lookup` / `search` / `relevant`：读取本机静态知识库。

典型调用顺序是读取状态，选择 `legal_actions` 中的一个 ID，按原样提交该状态的版本和 ID，然后等待状态变化。服务器先验证版本和动作；Mod 领取后在 Godot 主线程重新构建实时状态，并再次验证完全相同的版本和 ID。过期、重复、未知或暂停状态下的动作都会被拒绝。断开服务器只会让 Mod 等待，绝不会自动结束回合。

## 当前状态内容

战斗观察包含 act/floor、run 目标、回合、玩家 HP/最大 HP/block/energy、手牌、抽牌/弃牌/消耗牌堆摘要、遗物、药水、powers 和敌人。敌人 `next_move` 提供可知的每击伤害、次数、总伤害、状态牌数量，以及 defend/heal/buff/debuff/summon/hidden 标记。

语义动作目前覆盖：

- 战斗中用 `TryManualPlay` 打牌、选择普通目标、使用药水和结束回合。
- 地图可达节点。
- 事件、休息处、宝箱、奖励、常规卡牌选择和继续按钮。
- 商店打开、关闭、购买当前买得起的卡牌/遗物/药水和离开。
- 卡牌执行触发的 `ICardSelector` 嵌套卡牌选择；选择会作为新的 MCP 状态等待 Codex，达到最小数量后才提供完成动作，不会随机选牌。

## 安全和限制

Mod 拒绝多人 run，不修改存档数值，不注入作弊，也不对未知界面提供任意 generic click。主菜单只暴露受限的继续、单人标准模式和已解锁角色动作，不支持每日挑战、自定义局或任意菜单点击。所有 HTTP 调用都有有限超时；游戏动作只在显式逐帧循环恢复到 Godot 主线程后执行，不依赖 `_Process`。

第一版仍有明确限制：特殊卡牌的非卡牌二次目标、卡牌奖励 alternative、弃牌/药水替换、移除/升级/变换/附魔专用界面、部分特殊事件和版本新增 screen 可能只显示为无动作状态，需要在游戏 UI 手动处理。普通 overlay 卡牌选择目前暴露选牌和确认动作，但复杂多阶段 screen 仍应逐步观察，不能假设一次点击完成。没有运行游戏的自动化集成测试；发布前应在非关键单人 run 中手动验证战斗和各 screen。

## English summary

STS2 MCP Bridge is a local-only Mod plus BCL-based MCP stdio/HTTP server for Slay the Spire 2 `v0.107.1`. Build and install with `./scripts/build-install.sh`, configure Codex with the `dotnet run --project /absolute/path/Sts2McpBridge.Server.csproj --` command above, and start the game. Codex can continue a run or navigate the restricted Standard Single Player flow from the main menu. Every action requires an exact observed state version and legal semantic action ID, and the Mod revalidates both on the Godot main thread.
