# 原生高级序列启动即结束：取证、修复与部署

日期：2026-10-10，时间均为 UTC+8。维护版本：**0.4.0.262**。

## 原始故障

`.261` 的 N.I.N.A. 日志 `20261010-192733-3.2.0.9001.20320-202610.log`
显示，20:00:41 已通过 19:00 的原生等待指令；20:00:56 AIWeather 返回 Safe，
20:01:01 完成 WaitUntilSafe。随后整夜容器尚未开始第一个目标就抛出 WPF 跨线程异常：

```text
The calling thread cannot access this object because a different thread owns it.
Dispatcher.VerifyAccess
CanExecuteChangedEventManager.HandlerSink.OnCanExecuteChanged
SimpleAsyncCommand.RaiseCanExecuteChanged
UvexTargetObservationContainer.RefreshTargetEditorCommands
UvexTargetObservationContainer.RefreshPlanEditability
UvexNightSequenceContainer.RefreshPlanEditability
UvexNightSequenceContainer.Execute
```

原生序列在后台执行线程切换目标卡片的可编辑状态，但命令的 WPF 按钮订阅者属于
UI 线程。只做模型调用或静态 XAML 渲染，不能覆盖“实际按钮已订阅、后台开始执行”
的组合。20:01:18、20:01:20 再次开始时，整夜容器仍为 FAILED，原生策略没有重跑它，
因此表现为立即结束。这不是午夜截止、目标高度、曝光预算或天气门失败。

## 修复边界

- 目标卡片的属性/命令可执行通知派发到应用 UI Dispatcher；没有应用对象的离线
  WPF 测试保留创建线程 Dispatcher。执行线程上的编辑权限与异步导入 generation
  立即更新，不等待 UI，也不允许正在运行时提交旧导入结果。
- 整夜 lifetime 的清理范围扩展到初始化过程。即使首次 UI 通知或配置捕获失败，
  也不保留指向已释放 CancellationTokenSource 的活动标记；夜间独占预约正常释放。
- 不自动将原生 FAILED 状态变成 CREATED，不改变目标失败策略、设备身份、运动账本、
  屋顶/AIWeather、晨光与截止策略。加载/重置序列不代表旧物理运动已结算。

## 验证与部署

- 新增实际 STA/WPF Button 订阅加后台执行的正常结束、取消两个回归，以及初始化
  异常释放回归。验证 UI 线程通知、运行中禁用编辑、结束后恢复和夜间预约释放。
- 完整构建零警告、零错误，2,940 项 .NET 测试通过；Ruff 与 75 项 Python 测试通过。
- 四份冻结设计哈希、严格公开发布审计和 `git diff --check` 通过。
- 146 个生产 XAML 场景全部渲染，PNG SHA-256 与 `.261` 已检查场景全部一致。
  本次没有布局改动；渲染并不替代新增的线程回归。
- 安装前自动宿主空闲、无活动采集/主镜对焦。主 N.I.N.A. 设备均未连接，不能把
  未连接设备的默认 false 字段当作机械归位证明。PHD2 未运行；服务保持原状。
- 备份插件、Profile 和两份原计划后，20:06 正常退出 N.I.N.A.，没有强杀。
  41 个安装文件与精确构建产物逐一匹配，20:07:20 正常启动 N.I.N.A. 3.2.0.9001。
  PID 6816，后台桥确认已加载 `.262`，DLL SHA-256：
  `6E99DFEC7CF563CC151CF5A0C048FBA58B39C20F2AAF31549979CA4DA1D6A93D`。
- 通过 computer-use 实际查看自动观测、校准库与内嵌光谱单帧展开区；未点曝光、校准
  或机械按钮。没有另行启动观测，未重启 PHD2 或设备服务。

本地忽略证据：`output/sequence-dispatch-0262/`；安装前备份：
`output/deployment-backups/0262-20261010-200612/`。不提交原始观测或机器配置。

## 部署期间出现的另一条现场阻断

新进程日志 `20261010-200720-3.2.0.9001.6816-202610.log` 中，20:10:06 出现
另一次原生序列启动。本次维护没有发出开始命令。该次通过 WaitUntilSafe，确实进入
首目标 WR 134 的 Night Setup；没有再出现上述跨线程异常。20:10:07 因
`G3_MOTION_RECOVERY_CONTEXT_CHANGED` 停止，不能据此声称十目标已完成。

所引用旧运行 `UVEX-20261009T190953Z-7e0d4b1cc28c4da` 的目标是 HD 18552，
当地时间 03:09:53 开始、03:13:31 取消。其 G3 持久记录仍为未结阶段，最后记录
03:13:30 的出站修正约 664.3 arcsec（约 11.1 角分），等待新 G3 帧；不是已经
返回原点的 `SettledBudgetLedger`。新目标/计划与旧恢复上下文不一致，不能自动
采用旧目标的回程动作。其余九目标没有实际开始观测；原生容器显示 FINISHED
不代表取得科学帧，结果必须看逐目标及整夜记录。

此次只读取证，未删除、改写或退休这条旧记录，也没有重新加载以抹去现场状态。
需要操作者确认人工接管、曝光/导星/运动已停止后，按现有显式接管流程将旧记录
作为未完成历史保留，再单独重置/加载原生序列。不得通过重启、修改质量门或
直接编辑账本追认回程成功。软件安装及离线验证不是实天观测通过证明。

## 后续人工接管与序列重载（20:14–20:16）

操作者在上述说明后明确回复“处理，修复”，授权按人工接管流程归档旧恢复记录，
然后重载序列，不自动开拍。只读检查确认宿主 Faulted、没有主镜对焦任务、主
N.I.N.A. 设备均未连接、PHD2 未运行；独立测光服务所列任务均为 Completed。
断连读回不能证明机械位置，接管依据为操作者确认，不追认为自动回程证明。

通过 computer-use 操作现有“清除导星相机旧状态”按钮。20:14:39 归档两条 G3
canonical 记录：03:13 的未结修正，以及较早一条已结算记录。两份原 envelope
副本逐字节 SHA-256 均与操作前一致，旁存审计均为 `noMountCommandWasSent=true`、
`reportedMount.available=false`。canonical 恢复发现结果变为零；97 份 PHD2
历史文件哈希全部不变。没有改写原始 FITS、失败 manifest 或声称旧回程成功。
归档审计中的逻辑关闭状态不是物理到位或科学成功证据。

随后用 N.I.N.A. 文件加载按钮重载原前半夜十目标计划。只读 API 确认整夜容器、
十张目标卡片和所有内部阶段均为 CREATED，19:00 开始、00:30 截止、保持开顶待机
开启，晨光 -12° 与 WaitUntilSafe 保留。三个 WR 目标各 2×300 s，其余七个各
2×120 s；自动导星恢复开启、显式强制校准仍关闭。原计划文件哈希未改变。

20:15:20 日志另有一次发生于旧 FAILED 树上的瞬时开始/结束；本次维护没有按下
开始按钮。最终重载后的 CREATED 状态已独立核对，不把该次瞬时结束解释成修复后
的新目标运行。后台自动观测面板仍可展示 20:10 的历史 Faulted 结果，保留失败事实，
不等于 canonical 恢复记录仍存在。

本次无代码或插件二次改动，运行版本仍为已验证的 `.262`，不重复重启。
最终停在原生高级序列编辑页面，没有连接设备、曝光、导星、回零、Park 或开关顶。
本地核验证据为 `output/sequence-dispatch-0262/pre-retirement-inventory.json`、
`retirement-verification.json`、`reloaded-sequence-state.json`，不进入 Git。
