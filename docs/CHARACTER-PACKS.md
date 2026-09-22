# 角色包契约与制作工具（REQ-009 基础设施）

状态：契约 v1、离线校验器、创作者模板和两套适配测试已实现。**没有交付可运行的大头鹰 Live2D 模型，也没有把模板接入生产宠物或商店。** 原 v1.11 PNG 资源、养成规则和存档保持不变。

迁移阶段与真实模型验收见 [Live2D 重构计划](LIVE2D-REFACTOR-PLAN.md)。本文件说明已经能使用的包制作与验证接缝，不把离线检查等同于模型绑定、美术验收、SDK 授权或桌面 60 FPS 认证。

## 快速开始

1. 复制 `templates/character-packs/author-template` 到自己的作者工作目录；模板只有数据清单，没有假模型或占位贴图。
2. 修改 `characterId`、名称、骨架约定、实际参数 ID、归一化挂点、动作路径、服装映射与来源说明。
3. 在仓库根目录运行：

   ```powershell
   dotnet run --project tools/CharacterPackTool -- validate templates/character-packs/author-template/character-package.json --mode author-template
   ```

   合格模板返回 `ContractValid=true`，但 `FilesVerified=false`、`RuntimeReady=false`。退出码 0 只表示作者契约通过。
4. 按后面的制作步骤交付真实模型及其全部运行资源，填入每个文件准确的 `byteLength` 和 SHA-256，将 `kind` 从 `authorTemplate` 改为 `runtime`。
5. 执行离线运行资源检查：

   ```powershell
   dotnet run --project tools/CharacterPackTool -- validate D:/MyCharacter/character-package.json --mode runtime
   ```

   退出码：`0` 模板契约通过；`1` 清单/文件错误；`2` 文件检查通过但尚待可信 SDK 加载；`64` 命令用法错误。**当前 CLI 没有 SDK，不会把任何 Live2D 包输出为 RuntimeReady。** 模板直接用于 runtime 模式会得到 `TEMPLATE_NOT_RUNTIME`，而非成功。

校验只读取指定目录，不访问网络、不安装组件、不启动模型、不改存档，也不会自动注册资源或将商品设为可购买。

## 文件组织

```text
character-package.json          项目自己的稳定契约，不修改官方 model3 格式
runtime/avatar/
  character.model3.json         官方模型入口
  character.moc3                实际编译后的模型，不是改名 PNG/JSON
  textures/atlas.png
  motions/*.motion3.json
  ...                          模型引用的表情、物理、姿势和显示辅助数据
previews/neutral.png
source/
  character.psd                 可重新拆层/补图的原画
  character.cmo3                可编辑模型工程
  motions.can3                  可编辑动画工程
```

源工程位置用于交付追溯，不等于已验证源工程存在；运行版可以将源工程另包分发。是否具备修改和再分发权由作者核实，`sources.license` 的文字说明不会自动取得授权。

## 清单 v1

所有顶层字段必须显式存在，未知字段、重复 JSON 键和未支持版本会报错。清单上限 1 MiB、JSON 深度 32；数据对象为不可变记录/字典，不含脚本入口。

| 字段 | 含义与约束 |
| --- | --- |
| `version`, `kind` | 契约版本 `1`；`authorTemplate` 或 `runtime` |
| `characterId`, `displayName`, `packVersion`, `rigId` | 稳定业务无关 ID、显示名称、数字点分版本、共享骨架契约 |
| `backend` | `live2D` 或 `raster`；选择适配器，不按角色名称写条件分支 |
| `capabilities` | 眨眼、视线、呼吸、场景分层、换装、命中能力；声明的能力必须有对应参数/挂点 |
| `models` | 模型 ID → 官方入口、`rigId`、SDK/导出版本说明；同包模型必须满足同一骨架参数契约 |
| `actions` | `Idle` 与生产 19 动作的语义映射；缺能力显式标记 `unavailable` 或 `preview`，不能删掉条目假装完整 |
| `parameters` | 语义名 → 模型参数 `target`、有限 min/max/default 与控制者；不同语义不能隐式争用同一 target |
| `anchors` | `foot/head/body/bubble/mouth/leftHand/rightHand/bowl/keyboard/deskSurface` 必需；坐标为左上原点 `[0,1]`，不是 384×346 像素；可指定受模型驱动的 drawable target |
| `hitAreas` | 归一化矩形；声明命中能力须提供 `head` 和 `body` |
| `scene` | 固定语义层级、合成策略、body/foreground drawable ID；所有 pass 每帧共享一次模型更新 |
| `outfits` | 商品 ID → 模型、部件可见性、语义参数值、可选动作覆盖/缩略图/旧 PNG manifest 回退映射 |
| `resources` | 包内相对路径 → 数据角色、准确长度、SHA-256；作者阶段允许 `0/null`，运行检查不允许占位值 |
| `sources` | 拆层原画/模型/动画工程路径，以及作者和许可说明 |

`target` 是模型真实 ID，`parameters` 的键是业务无关的语义。模板里的 `ParammouthOpen` 仅为填写示例，不是 Core 中必须存在的参数名。`owner` 可为 `motion/blink/gaze/breath/outfit`；动作的 `controlledParameters` 明确其占用范围，宿主仍需实现实际叠加和抑制次序。

### 动作与六套服装

固定语义为 Idle、Yawn、Shy、Eat、WorkEnter、WorkLoop、WorkToBusy、BusyLoop、WorkExit、BusyExit、HungryEnter、HungryLoop、HungryExit、Tea、RpsRock、RpsPaper、RpsScissors、RpsWin、RpsLose、Annoyed。Bomb 等旧枚举不属于这版迁移基线。

时长与 `ClipCatalog` 一致，entry/exit pose 明确 standing/working/busy/hungry 的接口。三种猜拳完整动作还必须声明至少 0.86 秒的可辨认保持段。Idle 的业务时长为 0，呼吸/眨眼等连续效果由能力与参数控制，不强行伪造一个 PNG 帧数。

模板示范全部六个现有 `outfit.*` ID，显式提供互斥部件的 0/1 可见性。替换衣服不改变购买记录或收益；适配器确认实际生效前，准备中的选择不算已装备。

完整共享骨架下优先复用同一组 19 动作曲线；衣服仍需真正绑定、补齐边缘并逐套验收。若某套需要 motion 覆盖，放在该 outfit 的 `actionOverrides`，继续满足原语义时长。不同体型不承诺能穿同一套衣服。契约不硬编码某个角色必须拥有六套衣服，但首方大头鹰发布验收仍要求当前六套兼容。

### 工位与挂点

必须保留 `fire < character-body < desk-back < character-foreground < desk-front < computer`。Live2D 可选择 `shared-stage` 或 `split-pass`；后者必须共享同一参数/顶点快照，不能把手和身体分别跑物理。旧后端可用 `raster-legacy`，Live2D 不能借此声称复用颜色猜测遮挡。

`CharacterPackAdapter.PlaceAnchor` 将作者声明的初始归一化挂点映射到舞台矩形。带 `target` 的动态挂点仍须由真实 SDK 根据变形后的 drawable 求值；该离线工具不模拟顶点形变，也不证明手已经正确覆盖桌面。

## 三层验证边界

1. `ContractValid`：字段、语义、时长、范围、引用、路径、能力声明等一致。模板也可以达到此状态。
2. `FilesVerified`：实际文件存在，长度/hash 正确，角色扩展名/基本容器、官方 model3 引用、motion 时长等离线检查通过。PNG 头不是完整纹理解码；MOC3 magic 不是有效模型证据。
3. `RuntimeReady`：除前两层外，必须由**宿主提供的可信** `ICharacterModelProbe` 真正加载模型、纹理和运行数据，确认参数、drawable 和服装部件。包文件不能自己提供 probe 或填写“已通过”绕过此步。当前仓库没有生产 Live2D probe，所以离线 CLI 到这里会停在 `SDK_PROBE_REQUIRED`。

`CompleteCharacter` 另检查完整动作与场景/命中能力；`ReadyForCompletePet` 才同时要求完整能力和运行就绪。仅预览或缺动作包不能被当作完整养成角色。`CreateAdapter` 可在模板检查后用于数据预览，但此时 `CanPlay` 仍为 false，不能作为生产加载授权。

probe 得到 `VerifiedResources` 的只读内容快照，不重开会被作者修改的原目录。宿主加载也应消费这份快照；修改原目录后必须重新校验。运行校验不证明画风、动作自然度、授权或 60 FPS，通过后还需要迁移计划里的模型/交互/场景/性能验收。

### 文件安全

- 路径只能使用相对正斜杠形式的字母、数字、`_-.` 和目录分隔；拒绝绝对/UNC/盘符路径、反斜杠、`.`/`..`、空段、URL、百分号编码、ADS、Windows 设备名与尾点。
- 拒绝大小写碰撞。目录资源源拒绝根目录、祖先和路径组件中的 reparse point / symlink；不读取被链接到包外的资源。
- 运行文件只允许各角色对应的数据后缀：model3/moc3/PNG/motion3/exp3/physics3/pose3/cdi3/userdata3/WAV 或 raster manifest；不提供 HTML/JS/DLL/命令钩子。
- model3 的每个受支持 FileReferences 条目必须解析到已声明、已哈希的包内资源；额外资源引用种类报明确不支持，不能悄悄下载。
- 最多 512 份运行资源、总计 512 MiB；各文件另有角色上限，纹理尺寸 1..8192。检查长度时有界读取，不盲信声明。
- 文件源不是一个“安全网页服务器”。未来 WebView 宿主必须只发布已声明/已验证的快照，禁止把作者目录整体映射为任意网页、脚本或宿主对象的来源。作者检查也不代替多进程文件系统攻击隔离。

## 从原画到模型的工作流

1. 按原形象拆头、五官、上下喙/口腔、身体、左右臂与手型、脚、手持道具；补全原来被遮住的区域。
2. 制作可编辑 Cubism 网格/变形器，定义全身动作参数、眼部与嘴部变化，再把服装绑定到相同骨架。
3. 完成动作曲线、工作/饥饿循环接缝、猜拳保持、特殊眼睛切换；不要把整体淡化当作关嘴放手。
4. 导出官方运行文件，填入清单中的实际资源、参数、部件和挂点，计算准确 hash/长度，运行离线检查。
5. 在具有真实 SDK 的开发预览宿主中加载，验证动态挂点、手/桌遮挡、完整收尾与六套服装。当前工具只有契约适配预览，没有视觉 Live2D 预览窗。
6. 按重构计划完成连续播放、114 动作/服装、294 工位与 42 饥饿场景、真实窗口和故障回退验收后，再决定生产启用。

原 PNG、GIF 和 22 个服装拆片可提供形象/节奏/纹理参考，旧逐帧 UV atlas 不能直接变成 Cubism 骨架。官方资料：[原画处理](https://docs.live2d.com/en/cubism-editor-tutorials/psd/)、[运行文件导出](https://docs.live2d.com/en/cubism-editor-manual/export-moc3-motion3-files/)、[绘制顺序](https://docs.live2d.com/en/cubism-editor-manual/draworder/)、[遮罩约束](https://docs.live2d.com/en/cubism-editor-manual/clipping-mask/)。

## 可复现的适配回归

```powershell
dotnet run --project tools/CharacterPackSelfTest
```

`round.contract.json` 和 `tall.contract.json` 的参数名、模型/动作路径、初始挂点、drawable/服装部件 ID 和合成策略均不同。测试让它们驱动同一个现有 `ClipTimeline`，比较相同业务状态序列，并验证适配结果确实来自各自包，而非角色名分支。

测试使用内存合成的坏 MOC3 数据验证离线边界，**不是一个假装可运行的 Live2D 示例包**；既无 SDK 的检查，也有明确拒绝 probe 的检查。测试夹具不加入应用资源或商店。现有 PNG 与游戏功能测试应继续独立保留。
