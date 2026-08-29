# N.I.N.A. Advanced Sequencer 真正耦合路线规划

**状态：** 未来实施规划，不改变当前生产路线  
**形成日期：** 2026-08-28  
**依据：** 2026-08-28 上午对现有 Advanced Sequencer 容器、共享生产 runner、同步测光和中天翻转边界的源码审计

## 1. 目的

OpenAstroSpec Auto 最终不应在 N.I.N.A. 旁边再造一套完整的夜间调度器。N.I.N.A. 已经擅长表示：

- 目标顺序、开始/结束时间、循环次数和目标持续时间；
- 高度、时间、太阳/月亮等计划条件；
- 中天翻转及翻转后的目标恢复；
- 面向操作者的可编辑高级序列、模板、状态和历史；
- 由 N.I.N.A. 唯一拥有的 ATR585M 曝光生命周期。

OpenAstroSpec Auto 的不可替代部分则是：

- QHY 无运动广域见证与 fresh G3/PL3 大步入场；
- 稀疏星场的曝光阶梯、邻场重叠搜索和回程账本；
- LED/OFF 狭缝光学身份、实时黑缝中心线和目标入缝；
- PHD2 原生选星、校准、exact-lock、fresh residual 和脱锁恢复；
- ATR 光谱 ROI 的自适应曝光、光谱质量门和标定兼容性；
- 同步 QHY 测光后台任务与光谱帧时间关联；
- 上述动作的有界降级路线、不可变证据和崩溃恢复。

因此目标不是把全部内部逻辑平铺成几十个高级序列分支，而是让 **N.I.N.A. 编排“何时观测什么”，OpenAstroSpec 负责“如何可靠地把这个目标送入狭缝并维持科学状态”**。

## 2. 2026-08-28 上午审计得到的当前事实

当前 `OpenAstroSpec · UVEX4 目标观测` 已经是 N.I.N.A. 可识别的原生目标容器，并实现 `IDeepSkyObjectContainer`，但还不是真正可自由编排的高级序列：

1. `UvexTargetObservationContainer` 实现了 `IImmutableContainer`；用户不能增删或重排其内部动作。
2. 容器创建时固定插入 `ObservationRunCoordinator.Stages` 的 11 个阶段标记。
3. 每个 `ObservationStageMarkerItem` 没有独立观测参数，只把执行权桥接回内部 `ObservationCoordinatorHost` 和 `RealObservationStageRunner`。
4. 容器同时运行 N.I.N.A. 的 `SequentialStrategy` 和内部 coordinator；N.I.N.A. 子项主要承担可见阶段、状态和中断生命周期，实际降级路线仍由共享 runner 决定。
5. Dockable 与 Advanced Sequencer 已遵循 ADR-0009，共享同一个 `RealRunConfiguration`、`ObservationPlan`、runner factory 和 coordinator。这条“单一生产实现”不能在迁移过程中拆回两套。
6. 当前容器能利用 N.I.N.A. 原生目标名称、J2000 坐标、Conditions 和 Triggers 的外壳，但内部阶段仍像一层固定状态机套在高级序列里面。

这意味着当前实现已经完成“能从高级序列启动同一生产流程”，尚未完成“让 N.I.N.A. 原生序列承担适合它的调度与翻转职责”。

## 3. 职责边界

| 领域 | 未来主要负责人 | 理由 |
| --- | --- | --- |
| 目标列表、顺序、循环和时间窗 | N.I.N.A. Advanced Sequencer | 已有成熟编辑器、条件和序列持久化 |
| 高度/时间/太阳/月亮条件 | N.I.N.A.，OpenAstroSpec 复核本地墙体地平线 | 复用成熟条件；40° 围墙和整段最坏耗时仍是项目专用门 |
| 中天翻转的计划和赤道仪动作 | N.I.N.A. 原生翻转机制 | 不重写赤道仪翻转轮子 |
| 翻转前后的光谱/QHY/PHD2 协调 | OpenAstroSpec 专用触发器和恢复项 | 必须停止新科学帧、作废 pier-side 证据并重新入缝 |
| QHY 同步测光计划 | N.I.N.A. 序列中的 OpenAstroSpec 后台作业项 | QHY 仍由独立服务唯一拥有，不能切成 N.I.N.A. 主相机 |
| ATR 光谱曝光 | N.I.N.A. 相机 mediator + OpenAstroSpec 光谱曝光项 | 保留 ATR 的 N.I.N.A. 唯一所有权，同时用光谱 ROI 决定曝光 |
| PL3/邻场/LED/入缝/导星降级 | OpenAstroSpec 原子化获取项 | 这些是带 durable ledger 的闭环事务，不适合展开成大量用户可删的分支 |
| 屋顶、天气、镜盖和安全终态 | 共享 runner 与 N.I.N.A. 设备适配器 | 遵守 ADR-0010，不能由两个序列实现分别发命令 |

## 4. 建议的高级序列外观

第一目标不是把 11 个内部标记全部变成自由编辑项，而是形成少量、有明确契约的可组合项：

```text
N.I.N.A. Deep Sky Target Container
├─ N.I.N.A. 条件：时间 / 高度 / 目标顺序 / 循环
├─ OpenAstroSpec：准备并锁定本次目标
├─ OpenAstroSpec：获取目标、入缝并建立导星       [内部保留全部有界降级]
├─ OpenAstroSpec：启动 QHY 同步测光后台作业
├─ N.I.N.A. / OpenAstroSpec：ATR 光谱科学块       [自适应试拍 + 多帧计划]
├─ OpenAstroSpec：停止并关联 QHY 测光作业
└─ OpenAstroSpec：目标级收尾

N.I.N.A. 原生中天翻转触发器
└─ OpenAstroSpec 翻转屏障：停止新帧 → 翻转 → 全量重新入缝 → 恢复科学块
```

`获取目标、入缝并建立导星` 应保持为一个可暂停、可恢复、可审计的原子序列项。内部可以运行 G3 曝光阶梯、PL3、邻场搜索、N.I.N.A. 大步修正、LED 狭缝识别、PHD2 选星和 exact-lock；用户不能从高级序列中删除某一个回程或 fresh residual 步骤后仍让系统宣称“入缝成功”。

## 5. 中天翻转协议

不能只让 N.I.N.A. 翻转后继续下一张光谱。翻转必须形成一个跨两个系统的显式屏障：

1. OpenAstroSpec 停止发起新的 ATR 曝光，并按版本化策略等待或标记当前曝光。
2. 暂停 QHY 科学样本接纳；仍可保留原始帧，但标记为翻转区间。
3. 停止 PHD2 guide epoch，结清 exact-lock/回程 ledger，保存翻转前最后一份目标—狭缝残差。
4. N.I.N.A. 使用原生赤道仪逻辑执行中天翻转和目标重居中。
5. 翻转后立即作废所有依赖 pier side、相机朝向或旧 guide epoch 的证据，包括 PHD2 lock-shift、G3 像素运动模型、可选 QHY→G3 预置和旧狭缝投影。
6. 重新取得 fresh QHY 无运动见证和 fresh G3 WCS；重新执行实时 LED 黑缝检测、PHD2 原生选星/校准转换、exact-lock 和 fresh residual。
7. 只有新侧的目标—狭缝质量门、导星 settle、QHY 作业健康和 ATR 温度均通过，才恢复科学曝光。
8. 最终 manifest 为翻转前后建立不同 guide/placement epoch，并把每张 ATR 和 QHY 帧关联到正确历元。

N.I.N.A. 负责“何时以及怎样翻赤道仪”，OpenAstroSpec 负责“翻转使哪些光谱证据失效，以及怎样重新获得它们”。

## 6. 同步测光和光谱拍摄计划

### 6.1 QHY 同步测光

QHY 不能作为 N.I.N.A. 当前主相机加入普通曝光项，否则会破坏 ATR 唯一所有权。建议提供三类 OpenAstroSpec 序列项：

- `Start QHY Photometry Job`：锁定滤镜序列、曝光策略、cadence、目标和共同 run ID；
- `QHY Photometry Health Condition/Trigger`：把掉线、饱和、透明度和质量状态送给高级序列；
- `Stop and Correlate QHY Photometry Job`：有界停止后台作业并生成 ATR/QHY 中点重叠表。

后台作业必须响应 N.I.N.A. 的取消、翻转和安全触发器，但不得要求 N.I.N.A. 打开 QHY 物理相机。

### 6.2 ATR 光谱块

未来可把 ATR 科学块表示为一个 OpenAstroSpec 曝光容器：

- 开始时运行一次离散曝光阶梯；
- 用光谱 ROI 的饱和、线/连续谱 SNR 和目标/天空对比选择档位；
- 内部委托 N.I.N.A. imaging mediator 完成实际曝光和保存；
- 允许用户在高级序列中设置帧数、总时长、标定插入和结束条件；
- 每张曝光前检查 guide epoch、目标—狭缝 residual、QHY 健康和安全状态；
- 曝光档变化生成新证据，不把第一颗亮星的秒数硬编码给所有目标。

普通 N.I.N.A. `Take Exposure` 可用于不需要光谱自适应门的维护帧；正式光谱科学帧仍应通过上述专用项，以保证光谱 ROI 和入缝证据不会丢失。

## 7. 配置、哈希与序列编辑

高级序列可编辑不等于允许运行中热替换动作参数：

- 序列编辑结果在每次目标运行开始时捕获为新的 `RealRunConfiguration` 和动作哈希；
- 哈希只锁该次运行。修改“普通/超亮目标”、曝光阶梯或其他动作设置后，可以显式创建新运行；不能要求恢复旧哈希，也不能永久锁住后续测试；
- 当前运行中途修改的参数只属于下一次运行，除非先结束旧运行并使用“按当前设置新开一轮”；
- 尚未确认回程的真实运动仍须先恢复或由操作员显式接管，不能用改序列参数来重置累计位移预算；
- `.astroproj` 应保存人类可理解的策略选择和版本化配置引用，运行 manifest 保存执行时的完整不可变快照与 SHA-256；
- Dockable 和 Advanced Sequencer 必须继续通过同一 factory、plan 和 runner，不能各自解释哈希或恢复账本。

## 8. 分阶段实施

### 阶段 A：序列 API 适配层

- 固化当前 N.I.N.A. 3.2 使用到的容器、条件、触发器、中断、克隆和 JSON 序列化 API。
- 为后台 QHY 作业和翻转屏障定义不依赖 UI 的接口。
- 添加 `.astroproj` 往返测试，证明目标名、目录 ID、坐标、策略和条件不会丢失。

### 阶段 B：可编辑外层、原子化入缝

- 取消“所有内部内容都不可编辑”的单体外观；改成用户可组合的少量高层项。
- 仍将 acquisition/LED/PHD2/durable recovery 保持在一个原子项内部。
- Dockable 一键运行改为生成并执行与推荐高级序列模板相同的高层计划。

### 阶段 C：QHY 后台作业与 ATR 科学块

- 暴露启动、健康、停止和相关性产物；验证取消和进程崩溃。
- 让用户从高级序列设置帧数、时长和滤镜计划，同时保留 QHY 服务所有权。
- 生成精确的 ATR 帧—QHY 样本时间对应表。

### 阶段 D：N.I.N.A. 原生中天翻转耦合

- 先用模拟赤道仪验证翻转屏障、旧证据失效和重新入缝。
- 再以只读/无曝光回放验证 N.I.N.A. 翻转事件和恢复次序。
- 获得单独实机授权后，执行一次受监督翻转并跑到 `FinalizeObservation`。

### 阶段 E：多目标整夜模板

- 使用 N.I.N.A. 原生目标顺序、时间/高度条件和循环。
- 验证一个目标失败只暂停或按明确策略跳过，不损坏其他目标的 run、QHY job 或恢复账本。
- 最后验收 AIWeather/RRCI 故障触发、镜盖—停放—关顶终态和 Windows 通知。

## 9. 必须保留在 OpenAstroSpec 内部的复杂度

以下内容不应为了“看起来全部原生”而拆成大量可删的 N.I.N.A. 子项：

- 每一步预扣回程预算的 G3/入缝 durable ledger；
- PL3 解算阶梯与重叠邻场搜索；
- LED `OFF×3 → ON×3 → OFF×3` 时序和狭缝轮光学身份；
- PHD2 calibration/guide epoch/exact-lock/fresh residual 的因果绑定；
- 目标不可见、暗点源、紧致星云和超亮饱和目标的互斥降级分支；
- ATR 光谱 ROI 的自适应曝光判定；
- 崩溃恢复、动作回程和不可变证据落盘。

这些可以通过一个高层序列项的状态、预览、通知和证据链接对操作者透明，而不必把安全事务拆散。

## 10. 验收标准

真正耦合完成至少需要：

1. 用户能在 N.I.N.A. 高级序列里直观看到目标顺序、时间/高度条件、QHY 测光块、ATR 光谱块和中天翻转。
2. Dockable 一键入口与高级序列入口对相同输入生成相同锁定配置、阶段语义、降级路线和最终 manifest。
3. 修改下一次运行的分支不会热改当前运行，也不会被旧哈希永久锁住。
4. 中天翻转后旧侧的入缝/导星证据必然失效，并在新侧重新取得后才恢复科学曝光。
5. QHY、G3、ATR 和 COM5 的单一所有者不因高级序列组合而改变。
6. N.I.N.A. 取消、跳过、异常和安全触发都能传入 OpenAstroSpec 后台作业；后台失败也能以标准序列状态和 Windows 通知返回。
7. 模拟、replay、Dockable、Advanced Sequencer 和一次授权实机运行都通过 route-parity 门，且最后到达经过核验的 `FinalizeObservation`。

在这些条件完成前，当前固定容器仍是唯一生产实现；本文件只规定迁移方向，不能被解释为中天翻转、多目标调度或同步测光高级序列已经验收。
