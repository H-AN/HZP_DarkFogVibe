<div align="center">
  <a href="https://swiftlys2.net/docs/" target="_blank">
    <img src="https://github.com/user-attachments/assets/d0316faa-c2d0-478f-a642-1e3c3651f1d4" alt="SwiftlyS2" width="780" />
  </a>
</div>

<div align="center">
  <a href="./README.md"><img src="https://flagcdn.com/48x36/cn.png" alt="中文" width="48" height="36" /> <strong>中文版</strong></a>
  &nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;
  <a href="./README.en.md"><img src="https://flagcdn.com/48x36/gb.png" alt="English" width="48" height="36" /> <strong>English</strong></a>
</div>

<hr>

# HZP_DarkFog

基于 **SwiftlyS2** 的 CS2 单人曝光控制插件。  
插件通过 **HanZombiePlague API** 判定人类/丧尸与丧尸职业，为不同玩家单独创建 `post_processing_volume` 实体，实现“每个玩家看到的亮度不同”。

适用于僵尸服上线环境，支持：

- 人类曝光与丧尸曝光分离
- 按丧尸职业分组覆盖曝光
- 管理员命令强制设置目标玩家曝光（可持久）
- 隐藏命令静默设置当前玩家曝光（一次性）

---

## 特别感谢 :

<div style="display:flex; align-items:center; gap:6px; flex-wrap:wrap;">
  <span>技术支持 / Powered by yumiai :</span>
  <a href="https://yumi.chat:3000/">
    <img src="https://yumi.chat:3000/logo.png" width="50" alt="yumiai logo">
  </a>
  <span>(AI 模型服务 / AI model provider)</span>
</div>

<div style="display:flex; align-items:center; gap:6px; flex-wrap:wrap;">
  <span>SwiftlyS2-Toolkit & agents By laoshi :</span>
  <a href="https://github.com/2oaJ">
    <img src="https://github.com/user-attachments/assets/2da5deb4-2be9-4269-8f8e-df0029bb7c91" width="50" alt="toolkit logo">
  </a>
  <span>(开源 SwiftlyS2 Skills 与 agents)</span>
</div>

<div style="display:flex; align-items:center; gap:6px; flex-wrap:wrap;">
  <span>SwiftlyS2-mdwiki By LynchMus :</span>
  <a href="https://github.com/himenekocn/sw2-mdwiki">
    <img src="https://github.com/user-attachments/assets/c7f3b4ca-629a-4df9-a405-3f1a7507ecf2" width="50" alt="mdwiki logo">
  </a>
  <span>(开源 SwiftlyS2 mdwiki)</span>
</div>

---

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/Z8Z31PY52N)

---

## 功能概览

- 每位玩家独立曝光，不影响其他玩家视角。
- 通过 `HanZombiePlague` API 判断玩家是否为丧尸，不依赖 CT/T 队伍。
- 支持 `ZombieGroups`：按 `ZombieClassName` 单独覆盖曝光值。
- 默认曝光双档：`HumanExposure` 和 `ZombieExposure`。
- 管理员命令（默认 `fog`）可对指定玩家设置/重置曝光。
- 隐藏命令（默认 `hauhdahsdasd`）可静默设置自己曝光，不输出任何聊天提示。
- 配置热更新：自动重建职业曝光缓存、重注册命令、并重新应用在线玩家曝光。
- 断线、换图、卸载均会清理相关实体，避免残留。

## 曝光判定优先级

按以下顺序从高到低生效：

1. 插件总开关 `Enable = false`：移除玩家曝光实体（恢复默认）
2. 管理员手动覆盖（`fog` 设置过且未 reset）
3. `HanZombiePlague` 判定为人类：使用 `HumanExposure`
4. `HanZombiePlague` 判定为丧尸：
   - 若命中 `ZombieGroups` 对应职业，使用分组曝光
   - 否则使用 `ZombieExposure`

隐藏命令的曝光是**一次性即时应用**，不会写入管理员覆盖字典。  
也就是说它会在后续重算事件（如重生、感染、职业变更、地图切换等）被新的规则覆盖。

## 命令说明

### 管理员命令

- 默认命令名：`fog`
- 来源：`AdminCommandName`
- 权限：`AdminCommandPermission`

用法：

```text
!fog <player-name|playerid|@me> <exposure|reset>
```

示例：

```text
!fog H-AN 0.45
!fog 12 1.0
!fog @me reset
```

说明：

- `reset/clear/off` 都视为重置。
- 支持玩家名精确匹配、模糊匹配、PlayerID 匹配。
- 多目标命中会给出冲突提示，不会误设。

### 隐藏命令（静默）

- 开关：`HiddenExposureCommandEnabled`
- 命令名：`HiddenExposureCommandName`
- 设计用途：给其他插件“偷偷调用”来给当前玩家施加一次性曝光效果（例如道具）

用法：

```text
!<HiddenExposureCommandName> <exposure|reset>
```

行为：

- 仅当前玩家可用（Sender 必须是玩家）
- 全过程静默，不回复任何消息
- 只立即应用当前曝光，不写入持久覆盖字典

## 生命周期行为（当前代码行为）

- `player_spawn`：延迟 0.15 秒重算该玩家曝光
- `player_team`：延迟 0.15 秒重算该玩家曝光
- ZP 感染事件 `HZP_OnPlayerInfect`：延迟 0.1 秒重算被感染者曝光
- ZP 职业选择事件（母体/Nemesis/Assassin/Hero/Survivor/Sniper）：延迟 0.1 秒重算该玩家曝光
- ZP 回合开始状态事件 `HZP_OnGameStart`：延迟 0.2 秒全体重算
- 地图加载 `OnMapLoad`：延迟 1.0 秒全体重算
- 地图卸载 `OnMapUnload`：清理全部曝光实体，清空管理员覆盖字典
- 玩家断线 `OnClientDisconnected`：清理该玩家曝光实体，并移除管理员覆盖
- 插件卸载：注销命令、解除 API 事件订阅、清空所有运行态

## 依赖关系

- 必须：SwiftlyS2 运行环境
- 必须：`HanZombiePlague` 共享接口（键名：`HanZombiePlague`）
- 编译引用：`API/HanZombiePlagueAPI.dll`

说明：当前版本会在未发现 `HanZombiePlague` 接口时抛出依赖异常，属于强依赖设计。

## 配置文件

- 文件名：`HZP_DarkFog.jsonc`
- 配置根节：`HZP_DarkFogCFG`

### 全局字段

| 字段 | 类型 | 说明 |
|------|------|------|
| `Enable` | bool | 插件总开关 |
| `HumanExposure` | float | 人类默认曝光 |
| `ZombieExposure` | float | 丧尸默认曝光（未命中分组时） |
| `AdminCommandName` | string | 管理员命令名，默认 `fog` |
| `AdminCommandPermission` | string | 管理员命令权限 |
| `HiddenExposureCommandEnabled` | bool | 是否启用隐藏命令 |
| `HiddenExposureCommandName` | string | 隐藏命令名 |
| `ZombieGroups` | array | 丧尸职业分组曝光配置 |

### ZombieGroups 项

| 字段 | 类型 | 说明 |
|------|------|------|
| `Enable` | bool | 该分组开关 |
| `ZombieClassName` | string | 丧尸职业名（与 ZP API 返回值匹配，忽略大小写） |
| `Exposure` | float | 该职业曝光值 |

### 配置示例

```jsonc
{
  "HZP_DarkFogCFG": {
    "Enable": true,
    "HumanExposure": 0.01,
    "ZombieExposure": 0.01,

    "AdminCommandName": "fog",
    "AdminCommandPermission": "admin.dex",

    "HiddenExposureCommandEnabled": true,
    "HiddenExposureCommandName": "PPk0wBpK0m8W0sOi",

    "ZombieGroups": [
      { 
        "Enable": true,
        "ZombieClassName": "轻型丧尸",
        "Exposure": 1.0 
      }
    ]
  }
}

```

## 安装与使用

1. 将插件部署到服务器插件目录（包含 `HZP_DarkFog.dll` 与依赖）。
2. 确保 `HanZombiePlague` 插件先加载并导出共享接口。
3. 首次启动后检查/编辑 `HZP_DarkFog.jsonc`。
4. 根据管理体系配置 `AdminCommandPermission`。
5. 进服使用管理员命令测试目标设置，再验证隐藏命令是否静默生效。

## 上线前回归检查建议

1. 人类/丧尸基础曝光切换是否正确。
2. 多个丧尸职业切换时，`ZombieGroups` 是否按当前职业正确生效。
3. 管理员 `fog` 设置后是否在重生后仍保持（直到 reset/断线/换图）。
4. 隐藏命令设置后是否无聊天输出、且在后续重算事件被覆盖。
5. 模糊匹配多玩家时是否正确阻止误操作。
6. 换图后是否无残留曝光实体与脏状态。
7. 配置热更新后命令名、权限、曝光分组是否立即生效。

