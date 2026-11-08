# Validation / 验证范围

Repository checks cover the four-package layout, package manifest and dependency-example JSON, and local links in the root documentation. These are static checks; Unity compilation, Player builds and rendered output are outside this documentation update.

仓库检查覆盖四包目录结构、包清单及依赖示例 JSON，以及根目录文档中的本地链接。本次为静态检查，不包含 Unity 编译、Player 构建或渲染画面验证。

| Check / 检查项 | Scope / 范围 |
| --- | --- |
| Packages / 包结构 | FraxinusRP, Core, Shader Graph and Light Transport manifests / FraxinusRP、Core、Shader Graph 与 Light Transport 的包清单。 |
| Dependencies / 依赖配置 | [Local](manifest.local.json) / [Git](manifest.git.json) JSON and references to the four package directories / 本地与 Git 依赖示例的 JSON 及四包目录引用。 |
| Documentation / 文档 | Local links in the root README and `docs/` Markdown files / 根 README 与 `docs/` Markdown 文件中的本地链接。 |

Feature checks and their tested environments are documented with each package:

各功能的检查记录与测试环境见包内文档：

- [UltraRP validation / UltraRP 验证](../Packages/FraxinusRP/Documentation~/Validation.md)
- [Core APV checks / Core APV 检查](../Packages/Fraxinus-RPCore/Tests~/Unity2022APV/README.md)
- [Compute ray-query smoke checks / Compute 射线查询烟测](../Packages/Fraxinus-Light-Transport/Tests~/GpuSmoke/README.md)
- [Duckov adaptation sync / Duckov 适配回同步](DuckovAdaptationSync.md)
