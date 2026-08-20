# 已标定鬼影辅助定位（普通程序分支）

**状态：** 纯算法、确定性 G3 星源适配器、N.I.N.A. 三态工作流接线、可审计证据与离线 UI 场景已实现；真实安装仍未完成鬼影 commissioning，默认保持 `Skip`  
**默认运行模式：** `Skip`（关闭）  
**设计依据：** [ADR-0005](adr/0005-phd2-calibration-guided-slit-placement.md)

## 1. 结论与边界

普通程序可以利用光学鬼影辅助定位，不需要大模型看图，但前提比人工目视严格得多：程序只能匹配一个针对当前安装、当前 G3/PHD2 Profile、当前 ROI/binning/方向和当前 pier side 标定过的几何模板，并且必须在至少两张新鲜帧中同时通过几何、相对亮度、曝光归一化、共同运动、唯一性、边缘和不确定度门。

当前唯一实现的 extractor 是 `PointSourceStarFieldV1`，只支持能被确定性星源检测器稳定提取的紧致、近点状鬼影。环状、弧状、大片散射斑或会被星源检测器分裂的鬼影**尚未实现**，不能把人工目视“看得出来”当成已支持。将来若增加 annular/connected-component 后端，必须使用新的 backend/version 和独立标定，旧点源模板不得复用。

即使全部通过，结果的 authority 仍然只是 `CalibratedAuxiliaryOnly`：

- 鬼影不能单独证明“这就是目录目标”；本轮必须已有通过的新鲜目录/WCS 身份证据及其 SHA-256；
- 鬼影不能授权赤道仪运动；它只输出最新 G3 帧中的目标辅助质心和二维协方差；
- 后续仍须由 ADR-0005 选定的 PHD2 exact-lock 或独立 `G3PixelToMount` authority 执行有界动作；
- 每段动作后仍须 settle，并保存新 G3 帧重新测量目标/狭缝残差；
- 鬼影结果不能替代独立 C11 主镜对焦证据，也不能替代外部目录/WCS 身份链。当前 G3 WCS 失败时，只能借用同一运行内仍新鲜、SHA 未变且解算后赤道仪位置/pier side/坐标历元未漂移的 QHY WCS 身份；否则失效。

这条路径不包含目标名、目标坐标、今晚鬼影位置、两镜光轴差或某次人工偏移的源码常量。

## 2. 三态运行契约

正式 N.I.N.A. Profile/UI 已显式显示以下三种模式，并把选择写入不可变 action configuration hash 和运行 manifest：

| 模式 | 模板有效且多帧通过 | 模板缺失、过期或任一门失败 |
|---|---|---|
| `Skip` | 不检查、不使用 | 直接继续 G3 长曝光 WCS / N.I.N.A. 有界居中 / 小步重解 |
| `AutoIfValidElseSkip` | 使用带协方差的辅助质心 | 记录明确的跳过原因，然后自动走上述确定性回退 |
| `RequireValid` | 使用带协方差的辅助质心 | `PausedNeedsAttention`；这是唯一会因鬼影不可用而阻断的专家模式 |

默认必须是 `Skip`。`AutoIfValidElseSkip` 不得把一个失败的鬼影门变成整个采集阶段失败；`RequireValid` 也不得绕过外部目标身份、设备拓扑或新鲜度门。

对应纯决策 API 是 `GhostTemplateAssistance.Evaluate(...)`。返回值明确区分：

- `ContinueLongExposureWcsFallback`；
- `UseCalibratedAuxiliaryEstimate`；
- `PauseNeedsAttention`。

`GhostAssistanceResult.CanEstablishTargetIdentity` 固定为 `false`，防止调用方误把辅助坐标升级为身份 authority。

## 3. 模板标定内容

`GhostTemplateCalibration` schema 1 至少记录：

- `CalibrationId`、`SchemaVersion`、创建时间、有效截止时间；
- `InstallationEpochId`：拆装、重新同轴、准直、旋转相机后必须新建；
- G3 稳定 ID、PHD2 Profile ID、光学拓扑 SHA-256；
- extractor kind/version、`GhostSourceExtractionPolicy` ID 和完整内容 SHA-256；
- G3 ROI（未 bin 的传感器坐标）、X/Y binning；模板向量使用 bin 后交付图像像素；
- 方向 fingerprint SHA-256、数值方向角、pier side；
- 精确 gain 和经过实测的曝光范围；
- 一个或多个 `GhostTemplateFeature`：feature ID、`ghost - target` 相对向量、相对通量、二维向量协方差；
- 标定 RMS、最大残差和目标系统协方差；
- 原始标定证据 SHA-256；
- 对上述全部字段重新计算的内容 SHA-256。

运行时会重新计算内容 SHA-256。只修改 JSON 中的向量、pier side 或门限而不生成新标定版本，会直接使模板失效。

标定 RMS 被视为共享系统误差，加入最终目标协方差，不能因为匹配到更多 feature 就按样本数平均掉。每个 feature 的向量协方差和每个检测质心误差才作为独立项融合。

## 4. 普通程序怎样从 G3 图像得到候选

`GhostFrameObservationFactory.FromMonochromeFrame(...)` 直接消费已经由 PHD2 保存、由调用方验过 SHA-256 的内存 `MonochromeFrame`。它复用仓库已有的确定性 `StarFieldDetector`，不打开相机、不连接 PHD2、不调用 N.I.N.A. 设备、不发赤道仪命令。

版本化 `GhostSourceExtractionPolicy` 显式给出：

- 背景显著性阈值、质心窗口、边缘距离和最大源数；
- 星形椭率和饱和比例；
- 最低 SNR；
- 鬼影形态椭率门；
- 质心不确定度的上下限。

policy 的 ID、内容 SHA-256、extractor kind 和 backend version 同时写进 calibration 与 runtime binding。修改阈值、切换检测算法或升级 backend 都会让旧模板立即失效，因为这些变化可能系统性改变质心和通量。

适配器按像素位置稳定排序并生成 detection ID，同时输出用于 N.I.N.A. 叠加显示的 `GhostSourceOverlay`。`EvidenceSha256` 绑定原始帧 SHA、提取 policy、曝光/gain/时间、全部检测值和叠加字段；它不修改原 FITS。每个 `GhostFrameObservation` 还携带 backend/version、policy ID/hash 和 source-extraction evidence hash，模板匹配器会再次与 runtime binding 比较，不能用一组手填质心冒充另一套 extractor 的输出。

如果正式 runner 已经调用同一个 `StarFieldDetector`，可使用 `FromStarCandidates(...)` 避免重复检测。禁止用人工点击结果或大模型视觉描述伪造 `GhostSourceDetection` 作为无人值守输入。

## 5. 多帧模板匹配

每一帧对每个“检测源 ↔ 模板 feature”种子建立目标位置假设：

```text
targetEstimate = detectedGhostCentroid - calibratedGhostOffsetFromTarget
```

程序随后执行一对一 feature/source 分配和迭代加权拟合，并检查：

1. 匹配 feature 数达到 versioned policy 下限；
2. 每个 feature 几何残差和整帧 RMS 通过；
3. 观测通量除以模板相对通量和曝光时间后，在同一帧内一致；
4. 独立目标假设的 likelihood ratio 达到唯一性门，邻星形成的第二套相似 pattern 会使整帧失效；
5. 鬼影源和预测目标都不被图像边缘截断；
6. 二维协方差的最大特征值所对应一 sigma 不确定度通过。

至少两张帧还要联合检查：

- 同一批 feature 的位移能由一个共同平移解释；
- 如果已有一个**单独记账、已授权**的小步动作，其 expected detector motion 和证据 SHA 可作为共同运动参考；本算法本身绝不发起这个动作；
- 没有 expected motion 时，以拟合出的共同平移配准，并要求配准后的目标位置稳定；
- 不同曝光帧的 `flux / exposure` 对数尺度稳定；
- 整个帧序列时间跨度和每帧年龄不过期。

任一帧、任一跨帧门失败都不会留下可用目标质心。

## 6. 强制失效条件

以下任一变化都使旧模板不适用：

- `InstallationEpochId` 改变（拆装、重新同轴、准直等）；
- G3 stable ID 或 PHD2 Profile 改变；
- extractor kind/version 或 extraction policy ID/hash 改变；
- 光学拓扑 fingerprint 改变；
- ROI、binning、方向 fingerprint 或方向角越界；
- pier side 不匹配；
- gain 不同、曝光超出标定区间；
- 内容 hash 或标定证据 hash 不正确；
- 标定过期、帧过期、帧 SHA 缺失；
- 外部目录/WCS 身份不属于同一个 `ObservationRunId` 或目录 ID；
- 标定最大残差大于当前 policy；
- 邻星歧义、相对亮度不符、共同运动失败、边缘截断或不确定度过大。

相同 USB 设备名、人工觉得“鬼影还在差不多位置”或 plate-solve 给出的相机角度，都不能跳过这些失效条件。

## 7. N.I.N.A. runner 接入点

正式接线位于已有 `AcquireG3SlitField` 的普通目标/超亮目标身份定位失败位置。它不另拍鬼影帧，而是复用同一个无运动、最终安全关灯的 `OFF-before ×3 / ON ×3 / OFF-after ×3` 序列，从其中选择至少两张新鲜、实际曝光和 gain 相同的 OFF 帧。每张原 FITS 在提取前重新计算 SHA-256，原文件不改写、不移动、不删除：

```text
PHD2 保存 fresh immutable G3 full frame
        │
        ├─ 调用确定性 StarFieldDetector / GhostFrameObservationFactory
        ├─ 收集至少两帧并调用 GhostTemplateAssistance.Evaluate
        │
        ├─ UseCalibratedAuxiliaryEstimate
        │      └─ 仅把 centroid+covariance 交给已有 target/slit residual gate
        │          后续运动仍走 ADR-0005 authority、durable intent、settle、fresh residual
        │
        ├─ ContinueLongExposureWcsFallback
        │      └─ 现有曝光 ladder → fresh G3 WCS → N.I.N.A. bounded centering
        │          → bounded small-move/re-solve → fresh validation
        │
        └─ PauseNeedsAttention（仅 RequireValid）
```

PHD2 仍是 G3M2210M 和 guide pulse 的唯一所有者；N.I.N.A. telescope mediator 仍是目录/WCS 绝对运动的所有者。鬼影算法只是 Observatory 纯计算层，不能改变这个 ownership。

接线还强制保留以下独立 authority：

- paired OFF/ON/OFF 狭缝门必须通过，且实测狭缝残差仍在现有 commissioning envelope；
- Night Setup 中必须恰有一个、未过期且达到显式置信度门的 C11/G3 焦点 binding，Star Focuser Pro 在序列前后位置相同；当前饱和帧本身不作为焦点证据；
- 优先使用当前 OFF 帧成功的 G3 WCS 身份。若回退到 QHY，accepted FITS、WCS evidence、运行 ID、目标名/坐标、相机 stable ID、年龄和残差均须重验；
- QHY WCS 创建时额外快照赤道仪实报坐标、坐标历元和 pier side。消费前重新读取；pier/历元变化或球面位置差超过现有 2 arcsec 命令接受门即撤销身份；
- 有效鬼影结果写入 `G3FieldState` 的只是 target centroid/covariance。构造的辅助 target 不带 SNR/FWHM/flux，因此不会冒充普通 guide-star candidate；后续仍必须重新取得新鲜 target/slit/PHD2 residual。

完整 `GhostTemplateCalibration`、`GhostTemplatePolicy`、`GhostSourceExtractionPolicy`、当前安装/方向 fingerprint 和外部身份/焦点数值门放在 schema 4 `RealCommissioningPreset.GhostAssistance` 中。完整 preset 文件由 Profile 的 path + SHA-256 锁定；match/extraction policy 还分别有完整内容 SHA-256。非 `Skip` 模式缺任一项都会在真实运行预检阶段失败。机器默认值和目标特定常量不参与补全。

运行证据类型为 `g3-ghost-assistance`，包含模式、Calibration/Policy ID 与 hash、每张 OFF FITS/hash、提取 overlay、外部身份链、焦点/狭缝门、适用性、协方差、不确定度和最终决定。N.I.N.A. 面板显性显示相同摘要；离线 `ghost-assistance` 截图场景用于排版验收，不连接硬件。

## 8. Commissioning 与验收

1. 保持 Profile 为 `Skip`，在同一安装纪元和多个已知亮星上保存未改写的 G3 序列。
2. 覆盖实际支持的 ROI/binning、曝光范围、gain、两个 pier side、方向和温度/姿态范围；每个不兼容组合产生独立版本，不能外推。
3. 用新鲜 WCS/目录身份标出真实目标中心，拟合 ghost feature 相对向量、相对通量、协方差、RMS 和最大残差。
4. 必须加入负样本：邻星相似图案、薄云、鬼影靠边、旋转后、重装后、曝光越界、亮度比例变化和某一 feature 不随共同运动。
5. 先在 recorded frame/simulator 运行。只有 `AutoIfValidElseSkip` 的“有效时使用、无效时无动作并进入长曝回退”均可重复后，才进入 shadow、有限手动动作和闭环阶段。
6. N.I.N.A. UI 必须显示模式、Calibration ID/hash、安装纪元、适用性、匹配帧数、feature 数、唯一性、不确定度、最终决定和具体跳过原因；叠加图显示 detection ID、模板预测点与辅助目标质心。

相关源码与测试：

- `src/UvexAdv.Observatory/GhostTemplateAssistance.cs`
- `src/UvexAdv.Observatory/GhostSourceExtraction.cs`
- `src/UvexAdv.Nina.Plugin/GhostAssistanceCommissioning.cs`
- `src/UvexAdv.Nina.Plugin/RealObservationStageRunner.GhostAssistance.cs`
- `tests/UvexAdv.Nina.Plugin.Tests/GhostAssistanceRunnerSafetyTests.cs`
- `tests/UvexAdv.Observatory.Tests/GhostTemplateAssistanceTests.cs`
- `tests/UvexAdv.Nina.Plugin.UiHarness/ScenarioCatalog.cs`（`ghost-assistance`）
