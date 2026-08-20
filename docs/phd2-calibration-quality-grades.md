# PHD2 校准质量分级与择优使用

**状态：** 实现中的非冻结运行策略  
**策略标识：** `phd2-calibration-quality-v1`  
**适用范围：** G3M2210M / PHD2 导星、PHD2 runtime exact-lock 入缝及其证据；不改变设备所有权

## 1. 为什么不再使用“10°一刀切”

PHD2 的 RA/Dec 校准正交误差是重要指标，但它不是校准是否可用的唯一指标，也不是极轴误差的同义词。把 `10.0°` 当成唯一二元门会产生两个错误：

1. `9.9°` 可能同时存在错误 Profile、旧 pier side、异常轴速率、伪 settle 或过期入缝残差，不能因此自动成为科学权威；
2. `11.7°` 若 Profile、相机/赤道仪身份、方向、速率、真实 settle 和 fresh 入缝残差都可核验，可能仍是当前候选里最可用的一份，不应只因比 10°高 1.7°就完全停机。

本项目因此把正交误差作为多维质量项，使用四级结论。纯算法保留确定性的候选择优 API；当前生产适配器只读取并评估 PHD2 暴露的**一个活动校准**，不会把“单个活动校准已评估”冒充“已从历史库择优”。分级不会通过 PHD2 的 `Assume Orthogonal` 隐藏坏标定，也不会把降级候选静默写成正式无人值守科学标定。

## 2. 极轴误差与校准正交误差不能简单等同

本站设备的极轴维护可能约半年才进行一次，极轴不准和随时间增长的赤纬漂移是现实运行背景。软件必须容纳这种背景、记录导星表现和必要的降级，但不能据此把所有 PHD2 正交误差都归因于极轴。

- 极轴误差主要表现为系统性的赤纬漂移、长时间场旋及随天区变化的导星负担；它也会降低一份校准跨天区复用的可信度。
- PHD2 校准正交误差描述相机像素平面中实测 RA/Dec 运动轴偏离 90°的程度。回差、静摩擦、线缆牵扯、赤道仪机械误差、导星星质心、大气抖动、校准步数、相机/传感器旋转和校准过程中丢帧都可能影响它。
- 因此，`11.7° > 10°` 只决定它落入哪一个质量带，不足以单独证明“极轴坏”或“校准不可用”。相反，即使正交误差很小，错误 pier side、Profile 或 topology 仍然必须拒绝。

极轴状况应通过漂移/场旋和跨天区重复证据单独维护；PHD2 校准质量通过本文件列出的多维证据判断。二者可以相关，但不得互相代替。

## 3. 四级质量语义

| 等级 | 运行含义 | 是否可作无人值守科学权威 | 入缝动作限制 |
|---|---|---|---|
| `Excellent` | 年龄、正交性、方向/轴速、双向对称、身份、过程、真实 settle 和 fresh 残差均处于最佳带 | 可以，但仍须其它安全、目标身份和科学质量门全部通过 | 使用已锁定的正常单段上限；每段后仍需 fresh residual |
| `Qualified` | 有轻度偏差或 API 无法给出双向对称细节，但所有硬证据和运行验收通过 | 可以 | 使用正常单段上限；每段后至少一份 fresh residual |
| `DegradedSupervised` | 仍可导星，但例如正交误差位于 `10–30°`、旧校准缺少 pier/topology 过程 provenance，或 settle 丢帧处于降级带 | **不可以**；只有界面显式选择“有人监督降级”时可继续，manifest/帧质量必须保留降级标志 | 单段上限乘 `0.5`，残差门乘 `0.75`（更严格），每段至少一份 fresh residual，不允许自动无限重试 |
| `Rejected` | 身份/Profile/topology/pier 明确不匹配，校准缺失或过期，方向/速率无效，正交误差超过降级上限，settle 伪造/跨 epoch，或 fresh residual 不通过 | 不可以 | 禁止新的 lock shift、导星权威和 ATR 曝光 |

默认 v1 数值带由可序列化的 `Phd2CalibrationQualityPolicy` 保存并进入 action configuration hash：

- 正交误差：`≤5°` 为 Excellent，`≤10°` 为 Qualified，`≤30°` 为 DegradedSupervised，之后 Rejected；
- 校准年龄：`≤24 h`、`≤7 d`、`≤30 d` 分别对应三个可用带；
- 双向速率比：`≤1.20`、`≤1.75`、`≤3.00`；PHD2 event-server API 不提供该细节时，最高只能 Qualified；
- RA/Dec 跨轴速率比：`≤1.50`、`≤2.50`、`≤5.00`；这里比较两轴量级异常，**不假定两轴速率必须等于 1:1**。例如约 `2.0` 是需要显示的 Qualified 偏差，不会单独触发拒绝；
- settle 丢帧比例：`≤5%`、`≤20%`、`≤50%`；
- settle 和 residual 默认最长年龄均为 5 分钟；
- Qualified 单段比例/残差比例为 `1.0/1.0`，DegradedSupervised 为 `0.5/0.75`；
- 每个 exact-lock 阶段至少需要一份新的不可变 residual 帧。

这些数值是显式、版本化策略字段，不是散落在流程里的 `if (orthogonality > 10) stop`。调整策略必须生成新的 policy ID/配置哈希并重新测试，不能在运行中悄悄改门。

## 4. 必需证据

质量评估至少读取以下证据，并把每项理由呈现在 N.I.N.A. 的“质量门与证据”页：

1. PHD2 报告存在校准，RA/Dec 角度、速率、方向 parity 有限且有效；
2. exact Profile、G3 稳定身份、赤道仪身份与当前注册表证据一致；
3. calibration topology 与当前 Profile、ROI、binning、相机旋转/安装纪元一致；明确不一致为硬拒绝，历史过程无法证明时最多降级；
4. calibration pier side 与当前 pier side 一致；明确不一致为硬拒绝，未知时最多降级；
5. 校准时间戳和最大年龄；未来时间、未知年龄或超过降级上限不得使用；
6. 正交误差、轴速范围、方向和可得的正/负向速率对称性；RA/Dec 两轴速率彼此不同本身不等于双向不对称；
7. 由本客户端发出的 guide RPC、同一 connection/guide epoch 的 `SettleBegin → SettleDone`、成功状态、总帧/丢帧和完成时间；单独收到的旧 `SettleDone` 不能使用；
8. 最近一次 calibration、exact-lock 或显著采集运动之后的新 G3 帧 SHA-256、确认的目标身份、匹配 topology、目标到狭缝残差和允许门；
9. 质量策略 ID、全部阈值、最终 grade、分数、是否需要监督、允许的动作缩放和全部理由。

三个权限必须分开，不得用一个 `IsValid` 互相替代：

- `CanAttemptValidationGuide`：`PreGuide` 结构证据未被拒绝，可发起一次受限 guide/settle 验证；
- `IsLockShiftAuthority`：完成 `PostSettle` 复评且真实 settle、fresh residual 都通过，才可按 grade/缩放执行 exact-lock；
- `IsUnattendedScienceAuthority`：只有 Excellent/Qualified 且过程、topology、pier provenance 齐全时为真。Degraded 即使可做有人监督 lock shift，此值也永远为假。

正确顺序是：从 `PreGuide` 候选中选择最优 `CanAttemptValidationGuide` → 真实 settle → fresh residual → 重新 `Evaluate/SelectBest` → 再决定 lock-shift 和科学权限。旧 `ValidateCalibrationAsync` 只应用 policy 的 **Degraded 硬上限**；若仍把 Qualified 的 10°传给旧二元 validator，11.7°会在进入分级器之前错误地写入 `Failures`。`ApplyHardRejectionCeilings` 用于避免这个兼容陷阱。

## 5. 候选择优

`Phd2CalibrationQualityEvaluator.SelectBest` 按 `ValidationGuide`、`LockShift` 或 `UnattendedScience` 三种 purpose 分别选择，并保留每份候选的评估结果供 UI/manifest 检查，然后按以下稳定顺序择优：

1. 排除 `Rejected` 和只有 pre-guide 证据的候选；
2. `Excellent > Qualified > DegradedSupervised`；
3. 同级比较综合分数，优先较小正交误差、较新年龄、较低 settle 丢帧和较小 fresh residual；
4. 仍相同时按 candidate ID 排序，保证重放结果确定。

当前生产路径只把 PHD2 JSON-RPC 暴露的一个活动校准传入选择器，并在 UI/证据中明确写成 `single active calibration evaluated`。`SelectBest` 的列表接口是为将来的版本化历史候选库保留的纯算法能力；历史校准发现、加载、完整性校验和跨候选择优尚未实现，不能在运行文案中宣称已经发生。将来接入历史库时，每个候选仍须分别绑定 exact profile、设备、topology、pier、策略和不可变证据。

## 6. UI 与运行清单

界面不得只显示“校准有效/无效”。至少显示：

- grade 与策略 ID；
- 正交误差、年龄、两轴速率/方向、双向对称性可用性；
- exact Profile、设备、topology、pier side 的匹配状态；
- settle 是否由本轮 RPC 发起并属于当前 epoch、丢帧比例；
- fresh residual 的帧、年龄、像素值/门限；
- 完整的降级/拒绝理由；
- `DegradedSupervised` 的显式橙色监督状态、`0.5` 动作缩放和 `0.75` 残差缩放；
- “无人值守科学权威：是/否”。
- 当前候选来源；现阶段必须明确显示“仅评估一个活动校准，未加载历史候选库”。

`DegradedDirectTargetGuiding` 无论活动校准本身是 Excellent、Qualified 还是其它可用等级，都始终是有人监督退化模式，`IsUnattendedScienceAuthority=false`。没有本轮 action configuration 中显式的 supervised opt-in 时，必须在选星、guide、exact-lock 和科学曝光之前零动作阻断。

PHD2 runtime lock 的 durable outstanding 只在下一次显式 Execute/Resume 中恢复。取消/终结处理本身不发送运动。硬崩溃留下的 foreign nonterminal manifest 只有在新真实运行持有 OS 自动释放的 machine-wide owner lease、发现唯一 lineage/唯一 outstanding、旧 manifest 与当前 action/preset/context/device/topology/pier/policy 全部一致时，才可通过 fresh G3 场、fresh 同帧选星、PreGuide 分级、新 guide/settle epoch 和 fresh residual 重建返回语义；跨进程 epoch 数字绝不是连续性证明。无法证明端点时进入人工 reconciliation，且旧预算、动作数和最早时钟不得重置。foreign `SettledBudgetLedger` 是已接受的历史终点，不得复活成回程义务。

选择 `DegradedSupervised` 是本轮 immutable action configuration 的一部分。它必须进入 action hash、运行清单和 ATR/QHY/G3 质量事件；恢复或重启后不能凭旧 UI 选择自动沿用。若未显式选择，有降级候选时应进入 `PausedNeedsAttention`，而不是强制伪造更好的等级或无限重标定。

## 7. 实机 commissioning 顺序

1. 用 recorded/simulator 数据验证四个等级、候选择优和所有硬拒绝；
2. 只读检查当前 Profile、G3/赤道仪身份、registry/topology 和 pier side；
3. 在有人监督下执行一次真实校准，保留完整 PHD2 日志/事件和两轴正负向过程证据；
4. 选择导星星后取得本轮真实 settle，再取得 fresh target/slit residual；
5. 对同一候选生成最终 assessment 和 immutable JSON；
6. `DegradedSupervised` 只做短时有界测试，逐段缩小动作并复核 residual；
7. 在代表星场、两个 pier side、不同赤纬和翻转后重复，合格后才允许 Qualified unattended authority。

长期极轴维护频率低不构成绕过步骤 2–7 的理由；它只说明应更重视漂移、场旋、settle 和 fresh residual 的实时证据，并按完整策略判断当前活动校准是否可用，而不是死守单一正交阈值。
