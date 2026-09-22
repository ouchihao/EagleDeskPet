<div align="center">

# EagleDeskPet · 大头鹰桌宠

一只陪你上班、摸头会害羞、饿了要干饭，还能替 AI 和 GitHub 传话的桌面小鹰。

**Windows x64 · C# / WPF · v1.12.0-preview.1 · 最高 60 Hz 动画时间轴**

v1.12 预览版加入**图钉便签墙**和可选的 **Windows 原生提醒**：便签完成后归档、可恢复，提醒可进入系统通知中心。Live2D 已完成透明宿主可行性验证和角色包接口规范，但**还没有可用的大头鹰 Cubism 模型，也未替换现有逐帧动画**。功能、测试与未完成项见[本版验证记录](docs/VALIDATION-v1.12-preview.md)。

v1.11 加入装备属性、分币工资、递增成长和本地定时提醒；目录扩为 7 张桌子、7 种电脑、6 套形象及喝茶动作（含免费默认项）。旧存档保留余额、收藏、已得徽章和等级进度。价格与养成节奏见[经济规则](docs/ECONOMY-v1.11.md)。界面动效不替代宠物逐帧表演，也不承诺每台电脑的物理 60 FPS。

沿用 v1.10 的俱乐部界面和 v1.10.1 重绘的猜拳动作：出拳保持约 0.87 秒，补齐抬手、展开手势和收手；新分层服装跟随角色动作。历史验证见[界面升级](docs/VALIDATION-v1.10.md) / [猜拳修订](docs/RPS-ANIMATION-v1.10.1.md)。

[快速上手](docs/QUICK-START.md) · [便签墙](docs/NOTEBOOK.md) · [原生提醒](docs/NATIVE-REMINDERS.md) · [AI 一键接入](docs/AI-AUTO-SETUP.md) · [GitHub 通知](docs/GITHUB-NOTIFICATIONS.md) · [需求与路线图](docs/REQUIREMENTS.md)

<img src="docs/images/work-preview.gif" width="360" alt="大头鹰从工位入场、办公、进入忙碌到收工的动画预览">

*工位入场 → 持续办公 → 忙碌上头 → 收工。上图是历史 v1.8 的 20 FPS 演示 GIF，不代表新外观验收或程序的实际帧率。*

<img src="docs/images/v1.11-ox-work.png" width="220" alt="牛马也要体面：泡面董事会与赛博牛马工作站">
<img src="docs/images/v1.11-hero-work.png" width="220" alt="下班战神：云端摸鱼舱与黄金电脑">
<img src="docs/images/v1.11-astronaut-work.png" width="220" alt="摸鱼航天员：董事长的饼桌与资本家专用轻薄本">

*v1.11 的三套新装与工位：服装跟随动作，桌面在身体前方。以下截图均来自隔离测试，测试余额、收藏和进度不代表新玩家初始状态，不含桌面或私人内容。*

<img src="docs/images/v1.11-shop.png" width="760" alt="v1.11 鹰选好物：两位小数钱包、装备属性和同槽位比较">

*挑一张桌子，换一身心情。鹰币在本机积累，不花真钱；装备实际提高收入、经验并降低消耗。*

</div>

## 它能做什么

| 功能 | 小鹰的日常 |
| --- | --- |
| 桌面陪伴 | 透明背景、保持置顶、按住拖动、调整大小；待机时站立和打哈欠 |
| 摸头与喂饭 | 全身害羞、捧碗干饭；一碗完整吃完再续，开工 / 暂停不吞掉排队的饭；三摸护头，不额外刷奖励 |
| 挂机养成 | 积累粮食、饱食度、心情、经验和等级；没有死亡惩罚 |
| 工作赚钱 | 持续办公、满 30 分钟忙碌；基础每有效工作分钟 +1 经验和 +1.00 鹰币，生效装备提高效率、减轻消耗 |
| 鹰选好物 | 多列双排商品格、真实图标、分类 / 收藏筛选和翻页；独立预览，确认收藏后再装备 |
| 自主情绪 | 默认关闭；饿了抱空碗，心情低时闹别扭；场景有进入、循环、退出和冷却 |
| 猜拳小游戏 | 304×352 紧凑小窗，选拳后演出约 6 秒；出拳有清晰留姿，完整收手后揭晓，无下注 |
| 职场碎碎念 | 60 条普通短句 + 16 条情境短句，按工作、饥饿、低心情等选择，每分钟最多一句，可关闭 |
| 荣誉展览馆 | 6 个系列、18 枚金银铜徽章，按系列 / 获得状态分页；原薄荷桌和喝茶奖励保留 |
| 宠物俱乐部 | 日常陪伴、工位与钱包、消息与设置分为三页；奶油、薄荷和蜂蜜金主题，窗口与切页轻过渡 |
| AI 传话 | 可选 MCP 通知桥；一键配置本机 Codex、Claude Code、CodeBuddy Code 的结束提醒 |
| GitHub 消息 | 可选连接通知收件箱，提醒提及、请求评审、指派等动态，点击返回 PR / Issue |
| 任务小信使 | 按来源和任务更新、去重、未读和静音；只有来源明确报告才显示成功或失败 |
| 定时提醒 | 1–10080 分钟、一次或重复，最多 8 条；可独立开启 Windows 原生通知和宠物气泡 |
| 大头鹰记事本 | 图钉便签墙；新增、修改、完成归档、恢复，独立本地保存，不发送给 AI |

不接 AI、不连接 GitHub，也可以独立养宠物。普通养成和碎碎念不需要账号、API Key 或联网。

<details>
<summary>用图钉钉住今天的小事</summary>

<img src="docs/images/notebook-wall.png" width="760" alt="大头鹰记事本：软木墙、彩色便签和图钉，完成后收入归档">

支持 Ctrl+N 新建、Ctrl+Enter 保存和窄窗布局。活动便签与归档合计最多 256 条，每条最多 1000 个 UTF-16 字符单位；超过限制会提示，不会静默截断。详情见[记事本说明](docs/NOTEBOOK.md)。

</details>

<details>
<summary>到点提醒：喝水、起身，或记一件小事</summary>

<img src="docs/images/v1.11-reminder.png" width="480" alt="v1.11 本地定时提醒，可设置内容、间隔、重复、暂停与继续">

上图是 v1.11 的旧版界面。v1.12 增加独立通知开关、测试通知和系统设置入口。原生提醒默认关闭；首次启用会注册当前程序的系统通知身份，不需要账号。提醒只在桌宠运行时提交；重启后合并逾期消息，不后台唤醒设备，也不发送给 AI。勿扰、锁屏和系统权限可能隐藏横幅，提交成功不代表已显示或已读。

</details>

<details>
<summary>看看荣誉展览馆</summary>

<p align="center">
<img src="docs/images/v1.10-honors.png" width="760" alt="v1.10 荣誉收藏馆：六个系列十八枚徽章，分页查看">
</p>

截图使用隔离测试进度；每页展示部分徽章，实际需要达到对应条件后解锁。新增工位值班、装扮收藏和朝夕搭档，门槛见[快速上手](docs/QUICK-START.md#小卖部收藏与荣誉奖励)。

</details>

<details>
<summary>俱乐部面板与猜拳小窗</summary>

<img src="docs/images/v1.10-care.png" width="590" alt="v1.10 俱乐部面板：日常陪伴、工位与钱包、消息与设置三页">
<img src="docs/images/v1.10-rps.png" width="248" alt="v1.10 紧凑猜拳小窗，图形拳型按钮与双方对战展示">

猜拳中的小鹰仍在桌面完整表演；小窗负责选拳和提示，不替代角色动画。出拳 2.8 秒，输赢反应 2.4 秒（平局害羞仍为 2 秒），含 0.6 秒准备约为 6 秒；不包含你思考选拳、首次预载或上一动作收尾的时间。

</details>

## 开始使用

当前仓库提供完整源码和动画资源，**源码 ZIP 不是可直接运行的程序包**。可以按下方步骤构建；如果拿到已打包的程序，解压后运行 `EagleDeskPet.exe` 即可，无需安装 .NET。

`EagleDeskPet.Mcp.exe` 是可选 AI 桥接器，使用 AI 功能时与主程序放在同一目录，**不要直接双击它**。升级前先从旧版右键菜单退出。

| 操作 | 效果 |
| --- | --- |
| 按住左键拖动 | 移动小鹰，保存位置 |
| 单击 / 右键“摸摸头” | 温柔摸头害羞，短时连续摸头会抗议 |
| 右键“我的饭搭子” | 打开三页俱乐部面板，查看养成、工位钱包和消息设置 |
| 右键“开始工作 / 取消工作” | 入场办公 / 完整收工 |
| 右键“鹰币小卖部 · 我的收藏” | 逛商品货架、选分类或收藏；选中后独立预览、购买、装备 |
| 右键“石头剪刀布” | 在小窗选拳，演出约 6 秒；先收工再玩，不含预载与前一动作收尾时间 |
| 右键“自主情绪小剧场” | 开关自动情绪；“预览空碗小剧场…”为独立只读预览 |
| 右键“定时提醒 · 喝水与小事” | 设置内容、分钟数和重复方式，查看倒计时、暂停或删除 |
| 右键“大头鹰记事本 · 图钉便签墙” | 写便签、编辑、完成归档和恢复；不影响工作和养成 |
| 双击 | 有可返回的 AI 通知时打开配置的应用，否则打开养成面板 |
| 右键菜单 | 调整大小、置顶、暂停、显示帧率、开关碎碎念、查看荣誉与消息 |

初始有 5 份饭，每挂机 5 分钟积累 1 份，上限 99 份。基础每有效工作分钟获得 1 经验和 1.00 鹰币；工资逐分入账，不足 0.01 的余数跨重启保留，余额上限 999,999.00。无装备时每工作小时消耗 12 饱食度、10 心情；装备可减轻消耗并提升收益。连续工作 30 分钟进入忙碌，不额外翻倍；取消工作或饱食度到 0 后停止计薪并收工，退场动画不续发工资。

当前目录含免费默认项共 21 项。电脑价格从 120.00 到 3,840.00、桌子从 150.00 到 4,800.00，按档位翻倍；服装从 600.00 到 10,000.00，原“认真上班装”升级为“资本家套装”，旧买家不补差价。完整商品、属性和预计养成周期统一见[经济规则与商品表](docs/ECONOMY-v1.11.md#商品梯度)。商品必须通过资源完整性检查才可购买，“目录里有名字”不等于缺帧也能上架。

只有已拥有且真正换到小鹰身上的装备生效；预览和待生效选择没有加成，同槽替换不叠加旧件属性。商店展示单件属性与替换差异。薄荷桌也可吃满 30 顿饭免费获得，喝茶动作可升到 2 级免费获得；默认形象、桌子和电脑始终免费。

商店正常窗口每页 4 列 × 2 排，窄窗减少列数、矮窗减少排数，继续分页而不是变成长清单。商品格显示真实桌子、电脑、服装或动作图标；选中后查看价格、条件和当前状态。购买会弹出同风格确认小窗，明确商品、扣款和购买后余额；“先等等”、关闭或 Esc 均取消。

买到后永久拥有；预览、重复播放和重复装备不收费、不额外发奖。荣誉和购买走同一份拥有记录，不能重复扣款。

进度自动保存在本机，离线或长时间挂起最多补算 2 小时，在饥饿耗尽处截断。升级到版本 3 存档时，旧等级与级内进度映射到新经验曲线，不掉级、不追发历史工资，也不撤回已得徽章。“本次启动已赚”包括本次启动补算的工资，不因购物减少，不是钱包余额。关闭程序不等于取消工作，想结束这轮工作请先点“取消工作”。详细规则见[快速上手](docs/QUICK-START.md)。

定时提醒仅在桌宠运行时提交，不是 Windows 后台计划任务；退出不会后台唤醒。重启时逾期提醒合并通知，不补播每一次错过的循环。AI / GitHub 气泡优先，但不阻塞 Windows 原生通知；提醒不打断工作或改动养成数值。通知默认静音，不是持续响铃的闹钟。建议将 EXE 放在固定路径、以普通用户运行；移动路径会改变通知身份。详细边界见[原生提醒指南](docs/NATIVE-REMINDERS.md)。

## 从源码构建

需要 **Windows x64、Git 和 .NET 8 SDK**。运行时动画帧已随仓库提供，构建 EXE 不需要 Python、图像生成服务或 RIFE。

```powershell
git clone https://github.com/ouchihao/EagleDeskPet.git
cd EagleDeskPet
.\publish.ps1
```

如果系统对本地脚本有执行限制，请按你的设备或组织策略允许运行该脚本。也可以不运行脚本，直接执行：

```powershell
dotnet publish .\DuckDeskPet\DuckDeskPet.csproj -c Release -p:PublishProfile=win-x64 -o .\dist\v1.12.0-preview.1
dotnet publish .\EagleDeskPet.Mcp\EagleDeskPet.Mcp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\v1.12.0-preview.1
Copy-Item -LiteralPath .\docs\THIRD-PARTY-NOTICES.md -Destination .\dist\v1.12.0-preview.1\THIRD-PARTY-NOTICES.md
```

需要发给同事时，可将刚发布的目录打成带校验和的程序包（目标 ZIP 必须不存在）：

```powershell
.\tools\package_release.ps1 -ReleaseDirectory .\dist\v1.12.0-preview.1 -ArchivePath .\dist\EagleDeskPet-v1.12.0-preview.1-win-x64.zip
```

打包工具只收录两个 EXE、使用说明、第三方声明与校验和，逐文件复核 ZIP，不包含本机存档或账号配置。本预览版的功能与验证边界见[验证记录](docs/VALIDATION-v1.12-preview.md)；v1.11 的[发布说明](docs/RELEASE-NOTES-v1.11.md)保留为历史基线。

脚本从主工程版本读取默认输出目录，本版为 `dist/v1.12.0-preview.1/`；它拒绝非空目标，不删除或覆盖旧包，需要重发时指定新的 `-OutputDirectory`。手动发布也请先选择新的空目录。分发时将两个自包含 EXE 与 `THIRD-PARTY-NOTICES.md` 一起保留；脚本自动复制依赖声明，手动发布需执行上面的复制步骤。首次构建需要还原 NuGet 依赖。源码包含较多逐帧 PNG，因此克隆和发布产物都比纯代码项目大；构建缓存、EXE、存档和凭据不纳入 Git。

## 让它替 AI 和 GitHub 传话

### AI：MCP + 结束事件

右键小鹰 → **AI 自动传话 · 一键接入** → 选择客户端 → 预览路径 → 点击接入。安装器会先备份再合并自己的配置，保留其他 MCP / Hook；也可移除本安装器添加的条目。

- 支持本机 Windows 的 Codex、Claude Code、CodeBuddy Code 配置，不是给本地模型安装插件，也不是监听所有 AI 聊天窗口。
- MCP 提供 `pet_notify` 和 `pet_get_state`；自动新回复提醒通过客户端的 Stop 命令 Hook 发送同一类本地通知，不依赖模型每次主动调用工具。
- 客户端仍需审核 Hook / MCP 信任并开启新会话；Claude Code、CodeBuddy Code 的 Windows Hook 需要 Git Bash。
- 通知只弹小气泡，不会打断工作动画；返回入口打开用户配置的应用，不保证定位到精确会话。
- `Stop` 仅表示“回复就绪”，不代表任务成功。完整任务列表需要来源另外发送 `taskId`、递增 `revision` 和明确状态；GitHub 评审通知不会被猜成 CI 成败。

已有手动 Hook、自定义配置目录、组织策略等情况见[一键接入指南](docs/AI-AUTO-SETUP.md)。其他支持 MCP 的客户端可参考[手动配置与协议](docs/MCP.md)。

### GitHub：直接读取通知接口

右键小鹰 → **GitHub 消息**，在本机填写带 `notifications` 范围的 **classic PAT** 并验证连接。不需要 GitHub MCP，也不索取 GitHub 密码。

约每 60 秒检查一次，并遵守服务器轮询要求和限流；这不是实时推送。首次连接安静导入已有未读，后续更新才提醒。“本地全部看过”不会修改 GitHub 的已读状态。

Token 仅在应用中填写，**不要放进代码、Issue 或聊天记录**。设置、通知筛选和权限边界见 [GitHub 通知指南](docs/GITHUB-NOTIFICATIONS.md)。

## 动画是怎么做的

当前运行的仍是下述逐帧后端。Live2D 开发成果是独立的[透明宿主探针](docs/LIVE2D-P0-RESULTS.md)和[角色包契约、校验工具及模板](docs/CHARACTER-PACKS.md)，不是完成绑定的模型；模板不会被当成可售卖形象，也不能直接在主程序里换肤。完整迁移仍见[重构计划](docs/LIVE2D-REFACTOR-PLAN.md)。

角色不是把半身表情包直接贴到桌面，也不是用整图翻转或交叉淡化来换动作：

1. 根据角色参考生成全身关键姿势，保持脸型、配色和动作语义。
2. 使用 RIFE 光流插帧，再做透明边缘处理、脚点对齐与时序检查。
3. 导出连续 RGBA PNG，角色与桌子、电脑、火焰分层。
4. 在完整动作端点衔接；工作场景拥有独立的入场、循环、转忙和退场阶段。

哈欠、害羞、干饭每段 2 秒、121 个含首尾端点的采样帧；工位入场为 3.5 秒。普通眼睛转为忙碌黄眼时，经过闭眼再睁眼的关键姿势，不直接混合两套眼睛。

WPF 跟随桌面合成器，以最高 60 Hz 时间轴选帧，图片预解码后播放。**60 Hz 素材与调度不等于所有电脑恒定 60 FPS**；显示器刷新率、系统负载、远程桌面等都会影响实际表现。

v1.10 的窗口打开和面板切页采用约 180 ms 的轻过渡，遵循 Windows 减弱动画设置；高对比度下也停用这些动效。它们只作用于界面，不翻转、淡化或截断桌面小鹰的动作。v1.10.1 猜拳准备为 0.6 秒，出拳完整演出 2.8 秒，胜负反应 2.4 秒、平局害羞 2 秒，游戏阶段之间不额外站立等待；普通互动结束后站立 2 秒的规则不变。

默认形象与摸鱼卫衣使用逐帧资源；v1.11 的资本家、牛马、战神、航天员装使用绑定到角色动作的分层服装，桌子和电脑仍独立搭配。不是把一张不动的衣服盖在所有姿势上；资源缺失时不能购买，保存的已购内容也不会丢失。历史 v1.10.1 的三套帧包共 7476 张运行 PNG 的验证仅代表当时的素材；按需预载仍有明显内存成本。参见 [v1.9 素材说明](docs/ASSETS-v1.9.md) / [v1.10.1 猜拳验证](docs/RPS-ANIMATION-v1.10.1.md)。

[工作动画制作说明](docs/WORK-ANIMATION-ASSETS.md) · [v1.9 新动作提示词](DuckDeskPet/Assets/AnimationSources/expansion-v1-prompts.md) · [九枚徽章提示词](docs/BADGE-ASSETS-v2.md)

## 项目结构与测试

```text
DuckDeskPet/          WPF 窗口、互动界面、GitHub、客户端接入与动画资源
DuckDeskPet.Core/     养成规则、动作调度和通知逻辑
DuckDeskPet.SelfTest/ 核心逻辑自测
EagleDeskPet.Mcp/     MCP stdio 服务与一次性 Hook 通知入口
tools/               动画制作、资源检查和隔离集成测试
References/          原始 GIF 参考说明（原图仅在本机保留，不入 Git）
docs/                使用文档、制作说明和版本验证记录
publish.ps1          Windows x64 自包含发布脚本
```

`DuckDeskPet` 是早期保留的内部目录/命名空间；对外程序名统一为 `EagleDeskPet`。动作清单在 [actions.json](DuckDeskPet/Assets/actions.json)，旧白眼、炸弹等资源保留用于开发参考，不在当前待机动作池中。

在仓库根目录运行基础自测：

```powershell
dotnet run --project .\DuckDeskPet.SelfTest\DuckDeskPet.SelfTest.csproj -c Release
dotnet run --project .\tools\WorkStageSelfTest\WorkStageSelfTest.csproj -c Release
dotnet run --project .\tools\HonorSelfTest\HonorSelfTest.csproj -c Release
```

更多测试入口与适用范围见[开发与测试](docs/DEVELOPMENT.md)。当前猜拳修订结果见 [v1.10.1 验证记录](docs/RPS-ANIMATION-v1.10.1.md)，[v1.10](docs/VALIDATION-v1.10.md)、[v1.9](docs/VALIDATION-v1.9.md)和 [v1.8](docs/VALIDATION-v1.8.md)记录作为历史基线；模拟事件和自动化测试不代表已验证每种客户端版本、真实账号消息或长时间物理屏幕帧率。

## 数据与隐私

- 养成和设置存于 `%LOCALAPPDATA%\EagleDeskPet`，没有账号体系或云端养成存档。
- 便签存于独立的 `notebook.json`，提醒存于 `reminders.json`，都是本机明文，不发送到 AI 或 GitHub；不要保存密码或令牌。启用系统通知后，提醒正文也会进入 Windows 通知中心，锁屏可见性由系统设置控制。
- v1.12 提醒格式升级为版本 2；升级前退出程序并备份数据目录。不要让旧版读写已升级的提醒文件；回退时应恢复升级前备份。便签与养成存档相互独立。
- GitHub Token 使用当前 Windows 用户的 DPAPI 加密保存；通知标题、仓库名则保存在本地明文缓存中，最多 200 条，断开时可清除。
- MCP 使用当前 Windows 用户的本地命名管道，不开放网络端口、不读取聊天记录。任务正文和任务状态仅保留本次进程内存，不写入存档或 `pet_get_state`；自动 Hook 另保存用于去重的哈希文件名和随机轮次标识。
- AI 配置备份可能包含原配置里的密钥，不要分享备份、整个用户数据目录或客户端配置。
- 尚未实现任意角色包、好友联机、真实付费、更多职业或“专属下班仪式”；不要将需求池里的后续想法当作已提供功能。

## 素材与贡献

新想法、优先顺序和验收标准持续记录在[需求与路线图](docs/REQUIREMENTS.md)。REQ-001～008 的 Demo 功能与 REQ-010 便签墙已实现；REQ-011 原生提醒已接通，但真实通知点击和部分系统场景仍待人工验收。REQ-009 Live2D 尚未完成，不把契约或测试几何当成角色模型。跨 DPI、人工拖动和长期性能仍单列未测；后续 IDEA 保留备选或远期状态。

欢迎通过 [Issues](https://github.com/ouchihao/EagleDeskPet/issues/new/choose) 反馈问题或提出动作创意；新手可直接填写“功能建议”或“缺陷报告”表单。如何补需求、拆任务和提交改动见[参与指南](CONTRIBUTING.md)。报告动画问题时，附动作名称、版本、截图或短录屏会更容易定位；请先隐藏通知里的私人内容。

代码、AI 生成资源和原始表情包参考不是同一种授权对象。原始角色与表情包版权归相应权利人；原始 GIF 及其拼图仅在本机保留，不随 Git 仓库分发。公开代码和生成资源不代表授予原角色素材的商用许可，本仓库暂未指定统一的开源许可证。第三方库说明见 [THIRD-PARTY-NOTICES](docs/THIRD-PARTY-NOTICES.md)，参考素材见 [References](References/README.md)。
