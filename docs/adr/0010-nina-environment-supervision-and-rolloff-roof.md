# ADR-0010: N.I.N.A. 环境设备监督与平移顶生命周期

**Status:** Accepted  
**Date:** 2026-08-28  
**Decision owners:** Observatory owner and OpenAstroSpec Auto project

## Context

初始自动化基线把天气开顶和屋顶自动化排除在第一阶段之外，因此生产 runner 只能读取
N.I.N.A. 的 Safety Monitor、Weather、Dome 和 Flat Device 状态，不能完成真正的无人监管
开顶与收尾。现场后来安装并配置了两个独立 N.I.N.A. 插件：

- `AI Weather Advanced` 作为 `ISafetyMonitor`，在本机以从节点接收主节点的全天相机安全判定；
- `RRCI Advanced` 作为 `IDome`，把实际平移顶表示为 N.I.N.A. shutter/roof，并在本机以从节点
  接收主节点状态及受控命令。

N.I.N.A. Profile 同时可以选择 Weather Data 和主光路 Flat Device/Cover。此前前端虽然显示
四类门限，真实运行却只会主动连接镜盖；Safety Monitor、Weather 和 RRCI 是否连上依赖操作员
逐个点击。缺失能力、连接失败、明确危险和设备关闭也没有严格区分，导致有人监管模式出现
不必要阻断，而所谓完整安全链又没有屋顶生命周期。

## Decision

### 1. 环境适配器仍由 N.I.N.A. 所有

OpenAstroSpec 不直接调用 AIWeather、RRCI 的网络协议、配置文件或硬件驱动。生产 runner 只通过
N.I.N.A. 的 `ISafetyMonitorMediator`、`IWeatherDataMediator`、`IDomeMediator` 和
`IFlatDeviceMediator` 使用 Profile 当前选择。四个 DeviceId 在创建真实 runner 时进入不可变
`RealRunConfiguration` 和 action SHA-256；运行中选择漂移会在任何物理动作前阻断。

### 2. 两种监督模式具有不同的、明确的权限

**全无人监管（`NinaSafetyStack`）**要求四类适配器全部选择、连接且身份相符：

1. runner 自动连接 Safety Monitor、Weather、RRCI roof 和 optical cover；连接阶段不移动设备；
   若锁定的 RRCI 在连接时已经明确回报屋顶打开，本 run 从此承担终端安全收尾责任；
2. 首次开顶前重新检查 commissioning、Profile hash、owner selection、AIWeather safe、雨量、
   云量、风速、目标全程地平线、赤道仪 UTC 和 RRCI 身份；
3. 平移顶已关闭时，先让 N.I.N.A. 停放赤道仪并取得静止/park 回读，再由
   `IDomeMediator.OpenShutter` 委托 RRCI 开顶，限定时间内必须取得 `ShutterOpen`；
4. 同一运行中屋顶曾经打开后又关闭，不自动重开；必须重新开始并重新锁定运行；
5. AIWeather 在运行中由 safe 变 unsafe 时，立即请求中止 ATR 当前曝光，停止数据 owner，关闭
   镜盖、停放赤道仪、再关闭平移顶。雨、云、风或其他阶段门在已开顶时失败，也执行同一安全
   收尾；
6. 正常结束与终端故障都按“停止 QHY/PHD2 与狭缝灯 → 关闭主光路镜盖 → 停放赤道仪 →
   关闭平移顶”执行，并分别核验终态；取消、人工接管和 Dispose 仍不擅自启动新的机械动作；
7. RRCI 从节点必须另行配置新鲜主节点状态以及所需的 replica command/open authority。命令被
   RRCI 拒绝时停在明确错误，不绕过主节点安全策略，也不直接连接屋顶硬件。

**有人弱监督（`OperatorWeakSupervision`）**按 N.I.N.A. 实际选择逐项使用能力：

- 已选择的适配器自动连接；未选择、连接失败或某个 weather metric 缺失只产生 warning，其他
  可用能力继续生效；
- 弱监管不自动开关平移顶，屋顶仍归现场操作员；
- 已连接设备明确报告 `unsafe`、雨、超限云/风或关闭/错误的屋顶时仍阻断；已选中的可控镜盖
  仍按需自动打开并在收尾关闭，必须取得状态回读，无法打开或进入错误态时阻断；未选择镜盖
  才只 warning 降级；缺失不能被推断为安全，明确危险也不能借“弱监管”绕过；
- 高湿度在大丰近海台址只作为 advisory；100% 湿度本身不等于下雨或屋顶不安全；
- 所有降级、被使用的 DeviceId、连接结果和收尾结果进入运行事件与审计证据。本模式不得标成
  “无人值守”。

### 3. 安全事件和生命周期属于共享生产 runner

Dockable 和 Advanced Sequencer 不各自实现屋顶逻辑。上述行为只存在于 ADR-0009 定义的共享
`RealObservationStageRunner`，两种前端捕获相同不可变配置并执行相同阶段。组件脚本或后端
harness 的开关顶成功不能替代正式前端验收。

## Consequences

### Positive

- 操作员不再需要为一次全无人运行逐个连接 AIWeather、RRCI、Weather 和 Cover。
- 缺失设备与明确危险被分开，弱监管不再被“没有这个设备”无意义阻断，同时保留真实危险门。
- 平移顶只通过 N.I.N.A./RRCI 受控，并与镜盖、赤道仪 park、数据 owner 的顺序形成一个可审计
  生命周期。
- AIWeather/RRCI 从节点继续受主节点新鲜度和命令权限约束；OpenAstroSpec 不复制远程协议。

### Costs and limits

- 这是对冻结基线“屋顶自动化不在初始范围”的有意替代，需要同步更新基线与哈希。
- 完整无人路线依赖 RRCI 主/从节点、远程命令权限和 AIWeather 判定链正确 commissioning；安装
  插件本身不构成安全证明。
- 源码、模拟和只读状态审计不能证明真实屋顶运动。任何首次安装版本仍须在单独授权下按
  “只读连接 → 有界开顶/关顶 → 故障注入 → 正式前端完整收尾”分阶段验收，验收前只能声称
  “生产源已实现”。

## Supersedes

本 ADR 只替代基线第 9 节中“屋顶自动化和基于天气的开顶不在初始范围”的决定。设备单一所有者、
有界动作、不可变证据、弱监管不等于无人值守，以及 ADR-0009 的单一生产路线继续有效。
