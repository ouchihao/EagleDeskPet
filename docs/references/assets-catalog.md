# GIF 参考素材目录

可选本地参考目录：仓库根目录下的 `References/Memes/`。制作时共核验 39 个 GIF，原件未改动。公开仓只保留本目录中的清单与哈希，不分发原 GIF 或由原图拼成的预览。

这些 GIF 用来保留角色身份、神态和动作语义；大多数有背景、文字或身体裁切，不能直接作为透明全身桌宠动画。

运行资源需要重新生成全身关键帧，再经过透明边缘、脚点稳定、真实动作补间与 60 Hz 播放验证。

首批：Eat（干饭）是新动作；Shy / Bomb 保留已经验证的动画。桌子哭泣场景需单独的进入、循环、退出资源，不能把现有 GIF 直接拉伸代替。

如已取得这些素材的使用许可，可自行放入上述本地目录，再用 `tools/catalog_gif_references.py` 生成仅供本机查看的概览和动作联系表；缺少参考库不影响编译或运行桌宠。

| 文件 | 帧数 | 总时长 | 候选行为 | 排期 |
|---|---:|---:|---|---|
| no no no.gif | 30 | 1.80s | 表情库 | library |
| OK.gif | 24 | 1.44s | 表情库 | library |
| 不干了.gif | 24 | 1.44s | 表情库 | library |
| 乞讨.gif | 30 | 1.20s | HungryTable | P1 |
| 公司未来靠你了.gif | 40 | 1.60s | 表情库 | library |
| 压力山大.gif | 50 | 2.00s | 表情库 | library |
| 叉出去.gif | 18 | 1.08s | 表情库 | library |
| 口吐芬芳.gif | 25 | 1.00s | 表情库 | library |
| 吃惊.gif | 40 | 1.60s | Surprised | P2 |
| 咬牙切齿.gif | 18 | 1.08s | 表情库 | library |
| 哭.gif | 50 | 2.00s | HungryTable | P1 |
| 安排.gif | 36 | 2.16s | 表情库 | library |
| 害羞.gif | 30 | 1.80s | Shy | existing |
| 展开讲讲.gif | 23 | 1.38s | 表情库 | library |
| 工作.gif | 50 | 2.00s | Working | P2 |
| 干饭.gif | 20 | 1.20s | Eat | P0 |
| 开心.gif | 25 | 1.00s | Happy | P1 |
| 微笑.gif | 35 | 1.40s | 表情库 | library |
| 心好累.gif | 50 | 2.00s | 表情库 | library |
| 忙碌中.gif | 36 | 2.16s | Working | P2 |
| 成交.gif | 40 | 1.60s | 表情库 | library |
| 打起精神来.gif | 35 | 1.40s | 表情库 | library |
| 撒花.gif | 25 | 1.00s | 表情库 | library |
| 收到.gif | 50 | 2.00s | Acknowledged | P1 |
| 新年好.gif | 40 | 1.60s | 表情库 | library |
| 无语.gif | 35 | 1.40s | 表情库 | library |
| 明白.gif | 24 | 1.44s | 表情库 | library |
| 汗.gif | 50 | 2.00s | 表情库 | library |
| 没眼看.gif | 40 | 1.60s | 表情库 | library |
| 消消气.gif | 20 | 1.20s | 表情库 | library |
| 炸你.gif | 40 | 1.60s | Bomb | existing |
| 点赞.gif | 35 | 1.40s | Praise | P1 |
| 热烈欢迎.gif | 50 | 2.00s | 表情库 | library |
| 爱心眼.gif | 50 | 2.00s | 表情库 | library |
| 疲惫.gif | 36 | 2.16s | Sleepy | P1 |
| 赐予你力量.gif | 35 | 1.40s | 表情库 | library |
| 躺平.gif | 32 | 1.28s | Rest | P1 |
| 遇见大佬.gif | 35 | 1.40s | 表情库 | library |
| 飞吻.gif | 36 | 2.16s | Affection | P2 |

## 接入要求

- 不带入表情包的字幕、紫色底板、背景边框。
- 白色大头、暖棕轮廓、黄嘴黄脚、短棕身体的身份保持一致。
- 原地动作由肢体、嘴、眼睛和身体变形完成；不以整张图片翻转或淡入淡出替代。
- 目前普通动作持续 2 秒，首尾接同一 neutral，再站立 2 秒。
- GIF 时间粒度通常不是精确 60 fps；目标应用使用独立 RGBA PNG 帧并按 60 Hz 时间轴播放。
- 更完整参考并不代表一次性把全部 39 个动作都纳入首版；新动画需要逐个验收。
