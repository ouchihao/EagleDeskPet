# 动画素材与质量边界

## 本轮新增

- GIF 参考库：`docs/references/assets-catalog.json`，共 39 个原件、1,362 个原始帧；保留原件 SHA-256。原图不随公开仓分发，可选本地目录为仓库根目录下的 `References/Memes/`，验证只读、不修改原件。
- Eat：基于 `干饭.gif` 的行为及现有 neutral 的角色身份，用内置 imagegen 重新绘制 15 张全身关键姿势。
- 源图与完整提示词：`DuckDeskPet/Assets/AnimationSources/eat-sheet-v1.png` 与同名 `-prompt.md`。
- 最终动作：`DuckDeskPet/Assets/Animations/Eat/frame-0000.png` 至 `frame-0120.png`，384 × 346 RGBA，120 个时间间隔共 2 秒；首尾必须是完全相同的既有 neutral。
- 吃饭过程：取碗、捧碗、入口、鼓腮咀嚼、吞咽、收碗、放下手。原 GIF 的字幕、紫底和半身裁切不进入运行资源。

## 两个必须区分的质量问题

### 身体定位不能依赖被遮挡的肚子

碗挡住棕色躯干后，原通用躯干检测会把一侧翅膀误当身体中心，产生视觉漂移。Eat 使用底部两只黄色脚的中心和脚底接触点注册整套帧。这个覆盖仅在 `prepare_care_animation.py` 的独立进程中生效，不改变旧动作的定位规则。

### 121 个 PNG 不等于 121 个不同画面

RIFE 命令行的目录模式采样公式为 `i * input_count / output_count`。对两个关键帧直接请求 N 张输出，会提前到达第二张图，并重复其余段尾。这一点已核对 [RIFE 官方实现](https://github.com/nihui/rife-ncnn-vulkan/blob/master/src/main.cpp)。

本轮修正两个分段调用入口：请求 `2 × (N - 1)` 张，只保留前 N 张，对应完整且均匀的 `t = 0..1`。没有用图片淡入淡出、整身翻转或循环停帧补足帧数。

Eat 额外检查逐帧 RGBA 像素哈希与相邻重复；首尾相同是动作衔接要求，不应计为异常。最终测量以 `AnimationPreviews/Eat-qa.json` 为准。

旧的 Yawn、Shy、SideEye、Bomb 最终 PNG 在本轮保持不动。抽查旧 Yawn / Shy 各有 114 个不同像素画面；Bomb 有 107 个，其中包含段尾停帧。此处保留并明确现状，不把修复工具等同于旧资源已重新生成。后续重建需要重新检查炸弹轨迹、遮挡、脚点和暗底边缘。

## 验证与重建

需要 Python + Pillow + NumPy，以及 RIFE v4 模型。内置 imagegen 负责绘制；Python 仅复用既有离线切图、透明处理、补帧与诊断流程。

```powershell
python tools/test_care_assets.py
python tools/prepare_care_animation.py --assets DuckDeskPet/Assets --qa-only
python tools/prepare_care_animation.py --assets DuckDeskPet/Assets --rife PATH_TO_RIFE_EXE --rife-model PATH_TO_RIFE_V4_6
```

`--keys-only` 只准备关键帧供人工检查；检查完成后可以使用 `--reuse-keys` 跳过重复切图。重建命令只重写 Eat 自己的派生文件，不修改 canonical neutral 或旧四个动作。

暗底 contact sheet 和 GIF 是预览，不证明某台机器的实际显示帧率。运行时仍需验证 60 Hz 调度、资源预载、主线程响应和实际显示效果。

桌子哭泣场景仍是后续素材：需要独立的进入、循环、离开动作和稳定场景锚点，不能把现有半身 GIF 放大当成已完成全身桌宠动画。
