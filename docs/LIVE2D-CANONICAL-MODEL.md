# 原版分层模型与肩部修复

2026-09-30 · REQ-009 · 基础模型修复通过，生产 PNG 后端未切换

## 本次结果

修复用户截图中歪头时左肩露出透明缺口的问题，同时检查右肩、羽缘、脚踝和闭眼。原因不只是摆头幅度：旧分层沿可见轮廓裁切，头后没有足够肩部；外层头部连接修好后，内层脸部变形器还在独立移动下羽缘。

- 保留原版脸长、脸宽、轮廓与五官，不继续重绘已认可的外形
- 给双肩、上臂与胸口补隐藏底片，双翼上端增加约 21–23 像素冗余，中立时由原画完整遮住
- 胸口支点为原画坐标 `(193,215)`，`187..219` 区间连续软锚；45 个 XYZ 联合关键形保留实际摆头
- `pinFaceFeathers` 同时处理内层 `DeformFaceNinePose`：18 个下缘控制点固定，54 个上方控制点保持原值，中立关键形逐值不变，不靠关闭或缩小整体摆头来遮盖缺口
- 脚上沿补 4 像素同源重叠，避免纹理采样漏底；眼底从原脸取色，闭眼不再露出异色白矩形，眼睛分区不带走黄色喙边缘

本轮只验收基础模型及这些缺陷。极端姿态仍能看到少量原画的不透明羽缘阴影，不是透明镂空；后续动作美术验收仍需关注，不宣称所有边缘已达到最终成品质感。

## 原画与可复现文件

唯一中立外观基准为 [mascot-animated-neutral.png](../DuckDeskPet/Assets/mascot-animated-neutral.png)，来自哈欠原画首帧，不是另一个高分辨率 `mascot.png` 的缩图。原生画布为 384×346，15 个图层按原像素分区，不单独压扁头部。

- [分层 PSD](../DuckDeskPet/Assets/Live2DSource/Canonical/eagle.psd)、[中立图](../DuckDeskPet/Assets/Live2DSource/Canonical/eagle-neutral.png)、[像素一致报告](../DuckDeskPet/Assets/Live2DSource/Canonical/identity.json)
- [标准模型入口](../DuckDeskPet/Assets/Live2DSource/Canonical/Model/eagle.model3.json)、[作者断言及文件哈希](../DuckDeskPet/Assets/Live2DSource/Canonical/Model/authoring-validation.json)
- [打包 / 导出 / 回归命令](../tools/Live2DArt/README.md)、[绑定配置](../tools/Live2DArt/eagle-canonical-rig.json)
- [隐藏补绘来源及完整提示词](../DuckDeskPet/Assets/Live2DSource/PROMPT-v2.md)：内置 imagegen 生成的 v2 重绘稿未获外观采用，只用于被遮挡区域的底片；可见中立形象仍来自原始像素

独立解码 PSD 图层，清空缓存合成预览，再自行 source-over 合成：alpha 和全部可见 RGB 均为 **0 差异**。这证明中立原画保留，不代表放大的官方渲染截图逐像素相同，也不能替代动态接缝检查。

原始 RGBA SHA-256：`bfb4b0cba193d0a296070e9ec731f1629a10a261adb275b3a8d40d6506186e79`。PSD：`bc071b8fcbd1b3ccd85a0937808b6b31f3e3274260cf3414073d5e6740ec6c2b`。最终 MOC：`7d9dfe196097845446d506c24ec9d881ef044268d343b5e68a7bb3970863e5bc`。

## 官方运行与逐帧检查

[常规动画预览](previews/live2d-canonical-20260930.webm)使用真正的 Cubism Core / Framework 渲染，不是生成 GIF。检查使用同一个中立取景矩阵，没有逐帧缩放来隐藏漂移。

| 项目 | 实测结果 |
| --- | --- |
| 原版 PSD 回归 | 12/12：独立合成、单像素破坏检测、肩部重叠、脚踝及喙分区、隐藏底片尺寸与像素门禁 |
| 通用 / 旧 PSD 打包器 | 11/11 |
| 作者适配器 | canonical 18/18、旧 POC 9/9；旧 POC MOC 哈希不变 |
| 探针回归 | JS 20/20、隔离 CLI / 来源校验 11/11；构建 0 警告 / 0 错误 |
| 官方 Core / Framework | 15 固定姿态、15 网格 / 18 参数、MOC 一致性与实际绘制通过 |
| 连续驱动 | 12.191 秒采样、730 次绘制；rAF 间隔均值 16.703 ms、P95 16.8 ms |
| 联动压力测试 | 2 秒、121 次实际更新、8/8 精确 XYZ 角点，身体反向与呼吸联动，脚顶点位移 0 |
| 逐帧目视 | 提取并查看全部 103 个压力视频编码帧：双肩无透明镂空、脚无细缝、闭眼白块消失 |

运行时异常、WebGL 错误、非有限数值均为 0。压力测试每个角点保持 40 ms，报告按实际绘制样本统计精确角点，不能以经过八段时间或接近极值冒充八角点到达。

证据：[最终运行报告](validation/live2d-canonical-20260930.json)、[SDK / 模型来源哈希](validation/live2d-canonical-20260930-provenance.json)、[压力逐帧几何报告](validation/live2d-canonical-20260930-stress.json)、[压力录像](previews/live2d-canonical-20260930-stress.webm)、[肩部帧集 1](previews/live2d-canonical-shoulders-01.png)、[肩部帧集 2](previews/live2d-canonical-shoulders-02.png)。

重点姿态：[抬头并歪头](previews/live2d-canonical-head-up-left.png)、[低头并反向歪头](previews/live2d-canonical-head-down-right.png)、[闭眼](previews/live2d-canonical-eyes-closed.png)、[中立](previews/live2d-canonical-neutral.png)。

常规预览编码为 30 FPS；压力录像请求 60 FPS、实际编码 103 帧。rAF 和模型更新次数都不是显示器物理 Present 帧率，**尚未据此证明生产 EXE 稳定 60 FPS**。快速压力轨迹仅用于暴露缺陷，不是未来宠物待机动作节奏。

## 尚未完成的范围

当前只是采用原版形象的基础模型，未修改 `PetWindow`、PNG 动作、工资 / 存档 / 装备事务或生产发布包。19 段语义动作、六套服装及桌子 / 电脑 / 饭碗场景的完整迁移仍是下一阶段主线；张嘴参数原型不能算已完成打哈欠或吃饭。

鼠标靠近后头眼注视仅记为 **REQ-012 待办**，排在全动作迁移之后，详见[需求池](REQUIREMENTS.md)与[重构计划](LIVE2D-REFACTOR-PLAN.md)。

CMO3 的官方 Editor 打开 / 保存 / 再导出往返仍未验收，FREE 网格限制未绕过。作者报告保留 451 条既知对称性 / 层名告警及导出时的 `officialRuntimeValidation: pending`；独立的本次官方运行报告提供运行验证，不能把作者自检当作官方验证。仓库不新增 Cubism Core、SDK、Editor 或第三方 JAR，许可证与发布约束见[工具链记录](LIVE2D-TOOLCHAIN.md)。
