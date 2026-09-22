# Live2D P0 · 透明宿主验证与模型制作边界

日期：2026-09-22 · 关联：REQ-009 · **P0 部分通过，未完成 Live2D 迁移**

## 已实际运行

独立工具 [Live2DHostProbe](../tools/Live2DHostProbe/README.md) 使用 WPF `WebView2CompositionControl`，SDK 固定为 `1.0.4191.47`，目标为 `net8.0-windows10.0.19041.0`；本机既有 Evergreen Runtime 为 `153.0.4234.48`。没有安装系统组件，也没有把新依赖引入生产宠物的渲染路径。

测试内容是 WebGL 彩色几何体，不是鹰的替代画稿，更不是 Cubism 模型。绘制顺序覆盖身体、桌面、手、桌前沿、电脑；像素检查通过。深底、浅底和透明底的 WPF 自身渲染均保留正确透明度，WPF 标识能覆盖在浏览器画面之上。

首轮纯 `net8.0-windows` 虽编译成功，运行时缺 `Microsoft.Windows.SDK.NET`；改用带 Windows SDK 版本的 TFM 后通过，不能只以编译作为宿主可运行证据。

成功运行结果：

| 项目 | 本机结果 |
| --- | --- |
| 宿主加载至测试舞台 ready | 1.95 秒 |
| 5 项 WebGL 透明 / 语义层级像素检查 | 5/5 |
| 深色、浅色、透明 WPF 合成 | 3/3，均检查背景、前景叠层和浏览器内容 |
| 暖机后 10 秒 rAF 回调 | 599 次，59.95 Hz |
| 回调间隔 P95 | 16.80 ms |
| 超过 100 ms 的回调间隔 | 0 |

这里测量的是浏览器调度，不是物理显示器实际呈现，也不是完整模型的性能。原始报告与自身渲染保存在本地 `.codex-build/live2d-host-p0-20260922-b/`；可提交的无用户数据报告见 [P0 报告](validation/live2d-host-p0-20260922.json)。工具说明包含复现命令。

## 尚不能宣称通过的门槛

- 真实 Cubism 模型、裁剪蒙版以及模型与工位混合绘制；本次几何体顺序不能代替这些验证
- 实际鹰的 19 段动作、6 套衣服、7 桌 / 7 电脑及全部搭配
- 人工拖动 / 双击 / 右键 / 食物拖放、跨显示器 DPI、睡眠恢复、上下文丢失
- 10 分钟物理 Present 统计、2 小时工作 / 内存稳定性与无 Runtime 部署
- 专有 Core、模型和最终发布形态的授权审查

## 模型制作的实质性缺口

仓库受 Git 跟踪的资源没有 `.cmo3`、`.can3`、`.moc3`、`.model3.json` 或 PSD 模型源工程。常见安装位置和安装项中未发现 Cubism Editor；此结论不等于扫描或断言用户全部磁盘不存在工程。

官方 SDK 消费编辑器导出的模型；仅有纹理、PNG 动作帧或 JSON 参数不能生成合法的 `.moc3`。已核查官方外部 API 清单：它支持查询已有文档 / 参数、设置参数、接收导出事件，但该公开清单没有从零创建网格、变形器、关键形状并绑定新模型的完整自动化接口。事件 `NotifyMocFileExported` 是导出后的通知，不是模型生成器。[官方 API 清单](https://docs.live2d.com/en/cubism-editor-manual/external-application-integration-api-list/)、[官方模型导出说明](https://docs.live2d.com/en/cubism-editor-manual/export-moc3-motion3-files/)

本会话可用桌面控制能力不含原生编辑器操作，因此不能把“下载编辑器”当成能够完成专业模型绑定的证据。当前没有伪造 `.moc3`、把人形示例换皮冒充大头鹰，或将 PNG 拉伸动画命名为已完成 Live2D。

官方 Core 下载涉及专有软件条款；未来无限模型导入可能进入 Expandable Application 的单独发布审查。源代码公开、CDN 可访问都不自动意味着可重新分发。模型制作与许可需要落实后才能切换默认或公开发布该后端。[SDK 下载](https://www.live2d.com/en/sdk/download/web/)、[Expandable Application](https://www.live2d.com/en/sdk/license/expandable/)

## 继续推进的路径

当前继续完成与模型无关的角色包规范、离线校验、便签墙和原生提醒，保留生产 PNG 后端与全部存档。

后续可选：提供 / 制作带可编辑源工程的大头鹰 Cubism 模型，再完成真实 SDK 及组合验收；或经用户明确确认，另立可开源参数化 2D 路线。后一方案不宣称兼容标准 Live2D 模型，也不未经确认替换本需求。
