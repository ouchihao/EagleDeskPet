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

`tools/Live2DHostProbe` 使用 Microsoft.Web.WebView2 `1.0.4191.47`，受其 NuGet 包内 `LICENSE.txt` 的 Microsoft 软件许可条款约束。它不是 Cubism SDK，既有 WebView2 Runtime 没有被复制进仓库或桌宠发布包。诊断工具的版本与使用说明见其 README；本轮未引入或再分发专有 Cubism Core 或第三方 Live2D 模型。

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
