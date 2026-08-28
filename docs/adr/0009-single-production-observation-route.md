# ADR-0009: Single production observation route and field-test promotion

**Status:** Accepted  
**Date:** 2026-08-28  
**Decision owners:** Observatory owner and OpenAstroSpec Auto project

## Context

2026-08-24/25 的确定性 field harness 曾连续完成 Mirfak、Algol 和 M76 的核心闭环，
但安装后的 N.I.N.A. 面板仍保留了不同的阶段顺序、目标点、恢复条件、超时和清理动作。
因此“后端脚本已经跑通”并没有证明操作员点击的生产入口也能跑通。2026-08-27 的面板
运行随后逐项暴露了这些漂移：G3 WCS 指向画面中心而不是实时狭缝、邻场回程后仍要求
稀疏目标场解算、PHD2 重校准后复用旧 G3 几何、暗目标 ATR 探针直接暂停，以及可恢复
异常关闭主镜镜盖等。

继续让 field harness、Dockable 按钮和 Advanced Sequencer 容器各自维护一套完整观测
逻辑，会让一次成功只修好其中一条路线。用户真正使用的前端将继续承担没有经过同等
实测的分支、默认值和恢复状态。

## Decision

项目只允许一条生产观测编排路线：

1. N.I.N.A. Dockable 的“启动真实设备自动观测”和 Advanced Sequencer 的
   `OpenAstroSpec · UVEX4 目标观测`都是薄入口；二者必须从同一设置快照生成
   `RealRunConfiguration` 和 `ObservationPlan`，通过同一个
   `RealObservationStageRunnerFactory` 创建 `RealObservationStageRunner`，并交给
   `ObservationCoordinatorHost.RunAsync` 执行。
2. 阶段顺序、分支条件、曝光阶梯、超时、运动账本、失败恢复、清理动作和最终 manifest
   只能在上述共享生产组件中定义。XAML、Dockable 命令、SequenceItem、PowerShell 或
   `tmp/` 工具不得各自复制或修订一份端到端状态机。
3. 新的真实天空端到端测试应优先从上述两个生产前端之一启动。独立工具可以测试单个
   owner function、采集证据或在紧急 commissioning 中探索算法，但其成功只能称为
   **commissioning/component evidence**，不能称为“前端已修复”“一键流程通过”或
   “生产无人值守闭环完成”。
4. 独立工具发现的有效顺序、分支或恢复策略必须先移入共享生产组件，并用原始不可变
   FITS、sidecar、事件和 manifest 建立 replay/regression oracle；随后从前端入口重新
   验证。禁止只把脚本参数抄成另一个前端默认值，或长期保留“后端成功路线”。
5. Dockable 与 Advanced Sequencer 的任一差异必须局限于展示、N.I.N.A. 容器生命周期
   和用户明确选择的执行模式。它们不得拥有不同的科学/运动门、不同的恢复策略或不同的
   默认配置解释。

## Mandatory promotion gate

一次 field harness 或后端测试成功后，合并或交付前必须完成以下 promotion：

1. **固定证据边界：** 保存入口、构建版本、设置快照哈希、Night Setup/preset 身份、
   阶段序列、全部自动分支、人工/大模型介入标志和终态；原始观测文件保持不可变且不进 Git。
2. **建立差异矩阵：** 逐段比较测试入口与生产入口。每个差异必须标成“移入共享生产
   组件”“仅测试诊断”“生产有意更强”或“仍阻断”，不能用一句“功能等价”代替。
3. **移入共享实现：** 可复用行为进入生产 runner/协调器/服务契约；前端只负责捕获
   不可变配置、启动、显示、暂停、恢复、取消和接管。
4. **回放同一证据：** 自动测试必须覆盖成功分支及其相邻失败/恢复分支，特别包括超时、
   设备丢失、低信号、解析失败、导星重校准、运动回程和收尾。测试断言最终阶段，而不仅
   是某个 helper 返回成功。
5. **验证两个前端入口：** 至少用无硬件 replay/simulator 证明 Dockable 与 SequenceItem
   捕获相同配置、构造相同计划并调用同一 real-runner factory。任何入口绕过共享 runner
   都是发布阻断。
6. **安装精确 artifact：** 完成构建、UI harness、安装后的 N.I.N.A. 实例化和新日志检查。
7. **实机最终验收：** 当改变真实采集、运动、导星或恢复行为时，在获得单独硬件授权后，
   至少一次从正式前端单次启动运行到 `FinalizeObservation`；启动后若由临时脚本、人工或
   大模型选择方向、目标点、曝光档或恢复分支，则该次仍属于 commissioning，不是生产验收。

在第 7 步完成前，文档只能写“共享生产源已实现，安装/实机前端重放待完成”，不能把
后端成功升级为前端成功。测试工具可以作为历史 oracle 保留，但不得成为操作员必须另行
运行的隐形生产后端。

## Release and CI consequences

- 涉及端到端观测行为的变更必须同时更新/检查生产路径差异矩阵；只新增 field harness
  成功证据而未给出 promotion 状态时，不得关闭对应 issue。
- 源码测试应保护两个前端入口共同使用 `ObservationPlanFactory`、
  `RealObservationStageRunnerFactory` 和 `ObservationCoordinatorHost` 的事实，并保护
  canonical stage 列表一致性。
- 生产 manifest 应能区分 `Dockable`、`AdvancedSequencer`、`CommissioningHarness`
  等入口，同时记录 build/config hash；入口不同不能改变生产计划语义。
- “生产有意更强”的门必须有明确理由、测试和操作员可见解释。它若改变正常成功路线，
  仍须在正式前端重放，不能因为更保守就免验。
- 临时现场修复若来不及 promotion，应保持显式 commissioning 标签，不能静默写入面板
  默认值、台站模板或无人值守声明。

## Consequences

### Positive

- 天空窗口取得的成功会成为前端生产路径的回归 oracle，而不是另一套逐渐失效的脚本。
- Dockable、Advanced Sequencer 和自动测试共享阶段语义，减少“脚本成功、按钮失败”。
- 生产声明有清晰证据等级，避免把组件成功误报成完整一键流程成功。

### Costs

- 探索性 field harness 成功后还需要一次显式 promotion 和前端重放。
- 某些依赖 N.I.N.A. mediator 的路径需要可注入/replay 适配器，不能只靠 PowerShell 直接
  调 owner API。
- 真实设备行为改变后，在没有授权或天气窗口时只能报告“待前端实机验收”。

## Evidence

- [2026-08-24/25 实机收口](../commissioning-night-2026-08-24.md)
- [2026-08-25/26 M76 实机收口](../commissioning-night-2026-08-25.md)
- [2026-08-28 生产路线一致性审计](../production-unattended-route-parity-audit-2026-08-28.md)

