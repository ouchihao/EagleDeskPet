# 视觉参考与生成资源

大头鹰的角色神态和动作来自开发时提供的表情包参考，后续全身动作、工位分层和徽章另行生成、加工。

- [References/README.md](References/README.md)：原始参考与分发边界。
- [素材目录](docs/references/assets-catalog.md)：39 个 GIF 的文件信息和动作分类；原始文件未随 Git 分发。
- [生成来源](DuckDeskPet/Assets/AnimationSources/)：关键姿势、图像来源和完整提示词。
- [动画关键帧](DuckDeskPet/Assets/AnimationKeys/)：用于制作连续动作的关键帧。
- [运行时动画](DuckDeskPet/Assets/Animations/)：透明逐帧 PNG；实际加载项目由 actions.json 与 csproj 决定。
- [徽章 v2](docs/BADGE-ASSETS-v2.md)：九枚金属浮雕徽章的提示词与文件校验值。

原始 GIF 及其联系图仅保留本机供参考，未作为运行资源嵌入 EXE，也不随公开仓库分发。角色和原始表情包版权归相应权利人；生成资源不自动包含原角色的商用授权。
