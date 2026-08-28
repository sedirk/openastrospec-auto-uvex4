# 2026-08-28 AIWeather / RRCI 环境监督路线收口

## 目标与边界

本轮把已经安装并由 N.I.N.A. 选中的 AI Weather Advanced Safety Monitor 与 RRCI Advanced
平移顶接入正式自动观测生产路线，同时保留按设备逐项降级的有人弱监管模式。实现遵循
[ADR-0009](adr/0009-single-production-observation-route.md)：Dockable 与 Advanced Sequencer
只作为两个薄入口，共享同一个不可变 `RealRunConfiguration`、`ObservationPlan`、
`RealObservationStageRunnerFactory` 和 `ObservationCoordinatorHost`。

本轮没有获得开关顶、移动赤道仪、移动镜盖、采集或重启 N.I.N.A. 的授权，因此所有现场检查
均为只读；没有把源码通过冒充成实机无人验收通过。

## 只读发现

- N.I.N.A. 已加载 AI Weather Advanced 与 RRCI Advanced，并能枚举对应 Safety Monitor 与
  Dome 设备。
- 当前 Profile 选择：Safety Monitor `AIWeatherSafetyMonitor`、Dome/Roof
  `RRCIAdvanced.Dome`、Weather `NINA.OpenMeteo.Client`、主光路 Cover
  `ASCOM.GeminiAutoCover.CoverCalibrator`。
- AIWeather 与 RRCI 在此机均配置为从节点。RRCI Replica 不加载旧 RRCI 硬件驱动，符合屋顶
  单一所有者原则。
- RRCI Primary 默认不接受从节点动作；“允许从节点控制屋顶”和“允许从节点开顶”是两个
  独立、默认关闭的 Primary 权限。未完成有界现场验收前保持这种拒绝是正确的 fail-safe。

## 已实现的生产行为

### 全无人监管

1. 四类 N.I.N.A. 选择和开关顶策略进入 action SHA-256；运行中任何选择或动作配置漂移都在
   下一次物理动作前阻断。
2. 在连接采集 owner 前，自动连接 Safety Monitor、Weather、RRCI Roof 与 Cover。这个阶段
   明确不发机械命令；如果精确锁定的 RRCI 已回报屋顶打开，本 run 立即纳入故障关顶责任，
   不会因为后续相机/调焦器/导星器连接失败而把已开屋顶遗留在收尾链之外。
3. 开顶前重新核验 commissioning、设备身份、AIWeather safe、雨/云/风、全程地平线、赤道仪
   时钟和 Profile；赤道仪必须先停放并取得回读，随后才允许有界 RRCI 开顶。
4. 一旦本 run 接受了屋顶已开，屋顶随后关闭则不能在同一 run 自动重开。
5. AIWeather 的 live unsafe 事件会永久 trip 当前 run、请求中止 ATR 当前曝光，并串行触发
   “停数据 owner → 关主光路镜盖 → 停放赤道仪 → 关平移顶”。
6. 正常结束和已开顶后的终端失败共用同一有界收尾；任何无法回读的终态都报告 cleanup
   incomplete，绝不伪装成成功。

### 有人弱监管

1. 四类适配器按 N.I.N.A. 当前选择逐项使用：未选择、连接失败或缺少单项 weather metric
   只 warning，其他能力继续生效。
2. 弱监管从不自动开顶或关顶，也不能被标成无人值守。
3. 已连接适配器明确报告 unsafe、雨、超限云/风或屋顶关闭/错误仍是硬阻断。已选中的可控镜盖
   继续按需自动打开并在收尾关闭，必须核验终态；未选择镜盖才只 warning 降级，无法打开或
   明确错误仍阻断。“设备缺失”和“设备明确危险”不再混为一谈。
4. 高湿度仅 advisory；100% 湿度本身不构成终止条件。

## 验证状态

- N.I.N.A. 插件测试：`275/275` 通过，其中包含完整/弱监管设备选择、四适配器自动连接无运动、
  开顶前停放与有界等待、镜盖→停放→关顶顺序和 unsafe 事件收尾的回归。
- 本地全仓验证：冻结设计哈希通过，Release 构建 `0` warning / `0` error，`.NET 746/746`
  测试通过；独立 reduction 环境的 Ruff 通过、Pytest `66/66` 通过。
- 设计变更：新增 [ADR-0010](adr/0010-nina-environment-supervision-and-rolloff-roof.md)，并同步
  更新冻结自动化基线；远端 CI 结果由本次 Git 提交的 Actions 运行记录给出。
- 尚未完成：RRCI 主/从节点真实有界开关顶、AIWeather unsafe 故障注入，以及从正式前端运行
  至 `FinalizeObservation`。在这些项目通过前，状态只能写“生产源已实现”，不能写“全无人
  现场验收通过”。

## 下次有硬件授权时的最短验收

1. 只读连接四类环境设备并验证身份与状态失效；
2. Primary 仅授权一次从节点关顶，验证停放心跳和拒绝路径；
3. 再单独授权一次开顶，执行有界开→AIWeather unsafe→镜盖→停放→关顶故障注入；
4. 最后从正式 Dockable 或 Advanced Sequencer 启动一条短观测并跑到
   `FinalizeObservation`，检查事件、Windows 通知与终态证据。
