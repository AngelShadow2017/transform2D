# transform2D

[English](README.md) | 中文说明

（我偷懒用 AI 生成了英文版，这里是简化的中文版本。）

一个处于早期阶段的 Unity (C#) 项目，用来探索常见的 2D 仿射/几何变换：平移、旋转、缩放、以及围绕自定义 Pivot 的操作。

> 状态：早期 WIP。部分计划中的脚本与 Demo 还没写；存在历史的 mono 崩溃日志；缺少测试与详细文档。欢迎补齐！

## 目标
- 提供可复用的 2D 仿射变换工具（平移 / 旋转 / 缩放 / Pivot）。
- 简化局部 ↔ 世界、世界 ↔ 屏幕/UI 坐标转换。
- 用小型 Demo 场景直观展示概念。
- 借助社区一起完善：正确性、性能、文档与可视化。

## 贡献流程（Pull Request）
1. Fork 仓库  
2. `git checkout -b feat/你的功能`  
3. 编写/修改代码或文档  
4. 在 Unity 中简单运行验证  
5. Commit：`feat: add pivot rotation demo`  
6. Push 并提交 PR  
7. 根据 Review 调整  

### 编码/提交建议
- 函数尽量短小、语义清晰，数学工具尽量保持纯函数。
- 公共方法添加 `///` 注释。
- Demo 场景统一前缀：`Demo_`
- 避免非必要的大体积二进制资源。
- 目录建议：`Assets/Scripts/<类别>/`
- 新增脚本可加 SPDX 头：
  ```csharp
  // SPDX-License-Identifier: MIT
  ```

## 崩溃日志处理
`mono_crash.*.json`：  
- 需要排查可保留并总结；  
- 不需要时可在 PR 中迁移到 `Docs/CrashLogs/` 或删除。  

## 许可证
采用 MIT License，详见 [LICENSE](LICENSE)。

## 欢迎参与
喜欢数值、小工具、可视化或教学示例？非常欢迎提出 Issue / 提交 PR。  
如果这个项目对你有帮助，欢迎点个 Star ⭐。  
做大改动前请先开 Issue 讨论。  

PR 欢迎！
