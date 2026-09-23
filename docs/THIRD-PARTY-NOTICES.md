# 第三方依赖声明

## Windows 原生提醒增量（2026-09-22）

使用 `Microsoft.Toolkit.Uwp.Notifications 7.1.3`（未修改），上游 [Windows Community Toolkit](https://github.com/CommunityToolkit/WindowsCommunityToolkit)，MIT。这是现有 WPF / unpackaged 分发方式的兼容 API，并非微软针对新应用的最新推荐框架。

显式固定 `System.Drawing.Common 8.0.31`，避免兼容工具包默认拉取旧的 4.7.0；连同锁文件中的 .NET Foundation 传递组件按 MIT 许可使用。全部解析版本和哈希以 `DuckDeskPet/packages.lock.json` 为准。本轮 NuGet 审计在所配置源下未报告已知漏洞；不等于绝对安全保证。

Windows Community Toolkit 的许可原文：

```text
Copyright © .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the “Software”), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NON-INFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

System.Drawing.Common 及 .NET Foundation 组件的许可原文：

```text
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 开发诊断工具（不随宠物发布包分发）

`tools/Live2DHostProbe` 与 `tools/Live2DModelProbe` 使用 Microsoft.Web.WebView2 `1.0.4191.47`，受其 NuGet 包内 `LICENSE.txt` 的 Microsoft 软件许可条款约束。它不是 Cubism SDK，既有 WebView2 Runtime 没有被复制进仓库或桌宠发布包。

### Live2D 制作与模型探针（2026-09-23）

- `tools/Live2DArt` 固定使用 ag-psd 31.0.2（MIT）、sharp 0.35.4（Apache-2.0），`tools/Live2DModelProbe` 的本地 Framework 构建使用 esbuild 0.25.10（MIT）；锁文件记录实际解析的依赖，node_modules 不提交
- PSD2Live 1.1.1 是单独下载运行的 GPL-3.0 作者工具，不嵌入 WPF。直接链接其 API 的 `ExportPsdModel.java` 和 `ExportPsdModelSelfTest.java` 单独使用 GPL-3.0-or-later，完整条款见 [LICENSE-EXPORTER.txt](../tools/Live2DArt/LICENSE-EXPORTER.txt)；其余代码不因此改用统一许可
- 官方 Cubism SDK Web 5-r.5、Editor 5.3.04 仅保留本地开发副本。模型探针读取用户本地 Core / Framework，仓库和生产发布包不包含专有 Core、官方 SDK 源码、Editor 或第三方角色样例
- 鹰模型来自本项目参考形象生成的拆层图，原画、工具、模型输出与 SDK 授权分别记录；不宣称自动获得原角色商用权或可扩展应用发行许可

固定上游链接、版本、哈希和许可边界见 [Live2D 工具链](LIVE2D-TOOLCHAIN.md)。本轮没有购买或激活 PRO、没有采用限期 Alpha 工具。

## v1.8 · TOML 配置

本版为安全验证 Codex TOML 配置，使用 Tomlyn 0.19.0（未修改）。
上游：https://github.com/xoofx/Tomlyn
源码版本：ee3e3ca5b1f016b0db21ceb74d6c85d28a08ca16
许可：BSD-2-Clause。以下为该版本上游 `license.txt` 原文。

```text
Copyright (c) 2019-2022, Alexandre Mutel
All rights reserved.

Redistribution and use in source and binary forms, with or without modification
, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```
