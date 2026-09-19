# v1.9 动作、场景与三套形象资源

更新：2026-09-20。默认、办公服、摸鱼卫衣三套形象已完成，每套覆盖 19 段 / 2299 帧和站姿，共 6900 张运行 PNG；桌子 4 / 电脑 4 / 服装 3（含默认），另有喝茶动作。本文区分自动 QA、实际 WPF 合成及视觉复查；完整结果见 [验证记录](VALIDATION-v1.9.md)，玩法变化见 [本版更新](RELEASE-NOTES-v1.9.md)。

## 默认形象新增内容

动作语义、时长和帧目录以 [actions.json](../DuckDeskPet/Assets/actions.json) 为准。帧数按 60 Hz 采样并包含首尾端点，不是每秒重复静图凑数量。

| 片段 | 数量 | 时长 | 运行帧数 |
| --- | ---: | --- | ---: |
| HungryEnter / HungryLoop / HungryExit | 3 | 1.5 / 2 / 1.5 秒 | 91 + 121 + 91 = 303 |
| Tea | 1 | 2 秒 | 121 |
| RpsRock / RpsPaper / RpsScissors / RpsWin / RpsLose | 5 | 每段 2 秒 | 605 |
| Annoyed | 1 | 2 秒 | 121 |
| 新增合计 | 10 | — | 1150 |

饥饿桌子仍是运行时场景图层，不烘焙进角色帧；进入、最多两次循环、退出由场景时间线控制。猜拳 Prepare 不提前播放出拳图，结果等真实出拳序列结束后才揭晓；平局复用 Shy。默认自动待机池仍只有 Yawn，新动作不会因为清单存在或购买而随机自动播放。

每张桌子的前 / 后层属于一套商品，电脑是独立道具，沿用受校验的插槽和相对锚点。实际场景配置见 [work-scenes.json](../DuckDeskPet/Assets/SceneProps/work-scenes.json)。3 套形象 × 4 张桌子 × 4 种电脑的 48 种工作组合已进行真实 WPF 合成及接触表复查，具体采样口径见下文。

本轮追加目录 ID 为 `desk.walnut`（暖木复古桌，25 鹰币）、`desk.arcade`（闪电电竞桌，35）、`computer.retro`（奶油复古电脑，25）、`computer.arcade`（闪电小电脑，35）、`outfit.hoodie`（摸鱼卫衣，45）。价格以 `ContentCatalog` 为准；道具通过配置换装，卫衣使用完整角色帧，不是一张静态衣服。

## 完整办公服与摸鱼卫衣

[outfits.json](../DuckDeskPet/Assets/outfits.json) 中的 `outfit.office` 指向 [办公服 actions.json](../DuckDeskPet/Assets/Outfits/Office/actions.json)。采用整装角色帧变体，不是用代码画一张马甲贴上去；白色大头、棕色翅膀与橙脚保留，衣服随每个动作和遮挡一起绘制。桌子和电脑不随衣服烘焙。

`outfit.hoodie` 指向 [卫衣 actions.json](../DuckDeskPet/Assets/Outfits/Hoodie/actions.json)，使用淡紫卫衣、奶油色袖口 / 下摆 / 帽绳和小鱼图案。完整头部不被衣服替换，两套衣服分别拥有下表的全动作覆盖。

| 部分 | 动作段数 | 帧数 |
| --- | ---: | ---: |
| Yawn / Shy / Eat | 3 | 363 |
| 工作进入 / 循环 / 转忙 / 忙碌 / 两种退场 | 6 | 786 |
| 饥饿进入 / 循环 / 退出 | 3 | 303 |
| 喝茶、三种出拳、胜负反应、烦躁 | 7 | 847 |
| 合计 | 19 | 2299，另有 neutral 站姿 |

本轮 [办公服审计](../DuckDeskPet/Assets/AnimationPreviews/Office/outfit-audit.json) 记录运行 PNG 共 148200350 字节，约 **141.335 MiB**；[卫衣审计](../DuckDeskPet/Assets/AnimationPreviews/Hoodie/outfit-audit.json) 为 153846587 字节，约 **146.72 MiB**。两者均含各自站姿，不含制作源图、关键帧、预览与代码；这是 PNG 磁盘体积，不是进程内存或最终 EXE 压缩大小。

自动审计已通过 19 段最终 QA，并核对关键帧锁定、RGBA / 画布规格、脚点、透明边缘、来源哈希；10 处场景衔接端点像素一致，包括工作循环、转忙、两种退场及饥饿循环。每个普通动作也核对与套装站姿的起止关系。像素一致能证明接缝一致，不能单独证明动作好看、没有遮挡问题或实屏不卡顿。

卫衣同样通过 19 段最终 QA 与 10 处像素一致端点，并保留 [素材视觉复查](../DuckDeskPet/Assets/AnimationPreviews/Hoodie/visual-review.json)。真实三套完整解码验证累计读取 6900 PNG，102 项检查全部通过。

## 桌子入场遮挡与组合验证

角色身体、完整桌面、前景手 / 碗、桌前裙板、电脑使用稳定语义层级；桌子平移入场时不临时翻转整只宠物，也不把桌面放到身体后面。前景手 / 碗有独立遮罩，避免把身体一起抬到桌面上。

- 48 种工作组合覆盖六工作阶段、五个关键进度、深浅底、1.0 / 1.28 两种场景缩放；另测四张桌子的饥饿场景。共 6000 张 WPF 合成、221 张汇总图，失败 0；本轮已复查全部 48 组合接触表及卫衣交接。
- 三套 × 四桌的工作入场、普通 / 忙碌退场、饥饿入 / 退场，对完整动作逐帧检查；30480 张合成、840 页接触表，测得 912300 个桌身重叠像素中泄露身体像素为 0。它只证明所测桌身遮挡约束，不等于整幅图每一像素的审美认证。
- 最终自包含桌宠 9/9 联调包含所有追加商品的预览 / 购买 / 装备，以及卫衣哈欠和电竞工位办公。热态三秒 WPF 回调样本均值 59.67 Hz，P95 间隔 16.67 ms，最大 33.33 ms；没有测量物理显示器 Present。

证据分别保留在 `.codex-build/outfit-scene-all48-final/`、`.codex-build/desk-occlusion-all3-final/` 和临时宿主 smoke 目录，不随 Git 分发。场景缩放不是 Windows 跨屏 DPI；人工拖动、多屏和长时间稳定性仍未覆盖。

## 制作与复核入口

- [实际新动作与道具生成提示词](../DuckDeskPet/Assets/AnimationSources/expansion-v1-prompts.md)：内置图像生成器制作全身关键姿势，参考角色只用于外形与演技；原始 GIF 不作为运行资源。
- `tools/prepare_expansion_assets.py`：关键帧提取、脚点稳定、成对 RGB / alpha 光流插帧、透明边缘与时序 QA。
- `tools/prepare_office_outfit.py`：完整服装组装与审计；保留原生 alpha、关键帧和明确端点，不放宽 QA 阈值。
- `tools/prepare_hoodie_outfit.py`：卫衣完整动作生产、审计与清单；[实际提示词](../DuckDeskPet/Assets/AnimationSources/hoodie-prompts-v1.json)保留生成来源。
- `Assets/AnimationSources` 保存源图，`AnimationKeys` 及 `Outfits/Office/Keys` 保存关键帧，`AnimationPreviews` 保存 QA / 诊断图，运行时读取 `Animations` 中的受清单控制 PNG。

编译和运行不需要 Python、RIFE 或生成服务。重新制作用 `--help` 查看参数并提供独立 RIFE NCNN Vulkan 程序、模型及依赖；不在仓库中附带这些二进制，也不自动切换到付费 API。

```powershell
python .\tools\prepare_office_outfit.py --assets .\DuckDeskPet\Assets --phase audit
```

`audit` 读取已有资源并更新仓内的审计报告；需要纯只读检查时先查看现有 JSON。真实资源可用性另见开发文档中的 `OutfitSelfTest --resources-root`，完整解码、WPF 合成和真实宿主播放是不同测试层。

## 内存与帧率预算

PNG 预解码后远大于磁盘体积。以解码约 320×288、每像素 4 字节估算，不含对象、对齐、WPF / GPU 副本：

| 驻留内容 | 帧数 | 仅像素数据估算 |
| --- | ---: | ---: |
| 默认预热 Yawn / Shy / Eat | 363 | 约 128 MiB |
| 上述 + 三段饥饿 + Annoyed | 787 | 约 277 MiB |
| 开始工作额外六阶段 | 786 | 额外约 276 MiB |
| 同一套装全部 19 段同时解码（不是默认策略） | 2299 | 约 808 MiB |

火焰与道具帧、系统窗口、PNG 解码临时缓存、程序集资源、独立预览和换装时新旧缓存的短暂重叠还会增加内存；上表不是整机峰值实测。默认只预热常用三段，其余按需加载；工作和短互动帧可释放，换装完成才提交新套装，旧异步加载不能回填已释放缓存。

本轮三套逐一完整验证工具报告峰值工作集约 638.0 MiB，包含先前隔离 fixture，不能当作真实宠物日常内存或三套同时驻留的用量。

WPF 由桌面合成器驱动，时间轴最高 60 Hz；离线 GIF、采样帧数和数学端点测试都不能保证每台机器恒定 60 FPS。长期性能、远程桌面、多屏和跨 DPI 仍需独立验收。
