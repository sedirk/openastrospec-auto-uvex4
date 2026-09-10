# 超亮目标的未饱和翼部入缝分支

**状态：** 已实现，默认关闭；必须针对设备和当晚 Night Setup 显式调试后才能启用  
**适用范围：** G3M2210M 中目标核心即使在最短可用曝光下仍饱和，普通星形对焦与/或 G3 直接解析因动态范围不足而失败  
**不适用范围：** 普通目标、目标身份不确定、没有新鲜 QHY WCS、C11 焦点所有者/身份/拓扑/位置或当前质量证据已改变、多个相近亮源、翼部被边缘或邻星截断

## 1. 为什么需要独立分支

超亮目标可能出现一个正常但容易误判的状态：为看到周围星场而选择的曝光会让目标核心严重饱和；缩短曝光后，目标仍饱和但未饱和的 PSF 翼部足以给出稳定位置。这样的图像不能测量可信 FWHM，因此不能参与 C11 主焦点拟合，却仍可能在严格的身份和形态门之后用于入缝。

代码把两件事彻底分开：

- **焦点证据：** 必须来自独立、状态仍匹配的 C11/Gemini（N.I.N.A. `Star Focuser Pro`）证据，源相机必须是锁定的 G3；当前饱和帧始终记录 `focusEligible=false`。它不按日历自动过期，但所有者、设备身份/拓扑、焦点位置或当前质量门改变会立即撤销。
- **目标位置：** 只使用饱和核心之外、低于配置 ADU 上限的未饱和翼部；饱和核心只证明“这是一个超亮源”，不进入加权质心。

这个分支不是“解析失败就挑最亮星”。只有完整证据链通过时，G3 的 PlateSolve3 失败才可以作为已记录的降级事实，而不是被伪装成解析成功。

## 2. 授权证据链

每次使用必须同时满足：

1. 操作员在 N.I.N.A. 高级设置中显式启用 `BrightTargetWingCentroidEnabled`；默认值为 `false`。
2. 当前 QHY 接受帧属于同一个 `ObservationRunId`，FITS SHA-256 有效，且解算证据 JSON 本身也有 SHA-256。
3. QHY 请求目标名称、ICRS/J2000 坐标和 WCS 请求坐标与当前观测计划中的目录目标一致；目录 ID 不能为空。
4. QHY WCS 年龄和目标残差分别不超过显式配置的门限。
5. Night Setup 中恰好有一个 `C11Main` 焦点绑定；其 metric 必须是锁定 G3 相机给出的 `G3StellarShape`，证据 SHA-256、状态绑定和置信度均通过。
6. N.I.N.A. 当前 `Star Focuser Pro` 位置仍等于独立焦点证据锁定的位置；UVEX M2 和 GS350 的 ToupTek AAF 不能替代它。
7. PHD2 以显式配置的最短 G3 曝光拍摄一张新的 FITS；FITS 曝光元数据必须与请求一致，帧必须新鲜并有 SHA-256。
8. 配对 LED 序列已经证明狭缝位置、最后 LED-OFF 回读和 commissioning 几何范围；短曝光帧不会改变或重新猜测狭缝。
9. 未饱和翼部通过核心大小、SNR、角向覆盖、对向平衡、内外质心一致、边缘、饱和邻星、次峰/鬼影和全场唯一性门。

任何一项失败都返回可诊断的 `GateResult` 并停止自动入缝；不会因为目标“看起来很亮”而继续。

### 精调时星位与缝位的不同来源

从 `.148` 起，共享的 PHD2 精调和试拍前复核均使用“每张新帧测星位 + 本轮 LED
序列测缝位”，这同样适用于普通与暗弱目标，不只是超亮翼部模式。关灯后的星场
不必再次显出黑色狭缝；不得要求目标或天空把暗缝照亮。LED 参考须保持同轮次、
配置、相机/像素域、狭缝身份、连接代次及 UVEX 光路状态有效，并重新验证源文件和
识别证据哈希、关灯读回。三张新残差帧仍必须分别测得目标/导星位置，旧缝位参考不
授予旧星位复用权限；光路或设备状态改变时必须重新获取有效 LED 几何。

## 3. N.I.N.A. 可见配置

界面位置：`OpenAstroSpec 自动观测 → 高级设置 → 超亮目标：饱和核的未饱和翼部入缝`。分组默认折叠且开关默认关闭。

### 身份、时间与曝光门

| Profile 字段 | 含义 | 默认策略 |
|---|---|---|
| `BrightTargetWingCentroidEnabled` | 是否允许进入例外分支 | `false` |
| `BrightTargetMinimumG3ExposureMilliseconds` | 目标专用最短 G3 曝光，必须不长于常规 G3 曝光 | `0`，表示未调试并阻断 |
| `BrightTargetMaximumQhyWcsAgeMinutes` | 接受 QHY 帧/WCS 的最长年龄 | `0`，表示未调试并阻断 |
| `BrightTargetMaximumG3FrameAgeMinutes` | 最短曝光 G3 帧的最长年龄 | `0`，表示未调试并阻断 |
| `BrightTargetMaximumQhyResidualArcseconds` | QHY WCS 到目录目标的最大残差 | `0`，表示未调试并阻断 |
| `BrightTargetMaximumCatalogMismatchArcseconds` | QHY 请求/WCS 请求坐标与目录目标的最大差 | 通用严门，可显式修改 |
| `BrightTargetMinimumC11FocusConfidence` | 独立 C11 焦点证据置信度下限 | 通用严门，可显式修改 |

### 翼部形态门

| Profile 字段 | 含义 |
|---|---|
| `BrightTargetMinimumSaturatedCorePixels` / `Maximum...` | 合理饱和核心的像素数范围 |
| `BrightTargetWingRadiusPixels` | 从饱和核心向外检查翼部的半径 |
| `BrightTargetMinimumWingProminenceSigma` | 翼部相对稳健背景的最低显著性 |
| `BrightTargetMaximumWingLevelFraction` | 进入质心的像素相对饱和值的最高比例；更亮像素被排除 |
| `BrightTargetMinimumWingPixels` / `MinimumWingSignalToNoise` | 翼部采样量和总 SNR 下限 |
| `BrightTargetMinimumAngularCoverageFraction` | 八个角向扇区中必须被翼部覆盖的比例 |
| `BrightTargetMinimumOpposedWingBalance` | 相对扇区翼部通量的最低平衡度 |
| `BrightTargetMaximumWingCentroidDisagreementPixels` | 内翼与外翼质心允许的最大差 |
| `BrightTargetEdgeMarginPixels` | 防止画面边缘截断 PSF 的安全距离 |
| `BrightTargetNearbySaturatedCoreRadiusPixels` | 饱和邻星/分裂核心的排除半径 |
| `BrightTargetMinimumUniquenessRatio` | 最强与次强合格饱和源的翼部通量比下限 |
| `BrightTargetMaximumSecondaryPeakRatio` | 独立高信号次峰或鬼影相对饱和的上限 |

所有字段都进入不可变 `ActionConfigurationSha256`。运行中修改任一字段会被视为 Profile 漂移，不会改变已经锁定的运行。

## 4. Commissioning 流程

1. 保持分支关闭，在同一安装纪元、相同 G3 ROI/binning/增益、相同 pier side 下收集多颗已知超亮星的最短曝光序列；原始 FITS 不得改写。
2. 对每颗星从较短曝光开始，找出 PHD2/G3 能可靠实报的最短曝光。这个值必须来自当前设备实测，不能使用源码常量，也不能假定为 500 ms。
3. 同时保留新鲜 QHY 接受帧及 WCS 证据、目录目标、C11 独立焦点证据和 LED 狭缝证据。没有完整链条的帧只能用于离线算法观察，不能验收自动入缝。
4. 在记录帧上扫描翼部形态门。至少覆盖：单一目标、两个相近亮源、目标靠边、饱和邻星、反射鬼影、薄云、焦点稍差和无目标场。
5. 门限应选择为“有疑问就停”，并写入 commissioning bindings。禁止写入目标名、固定目标坐标、当前两镜光轴差或某次人工偏移。
6. 先在 simulator/recorded-frame 测试；然后只读连接；再执行一次有界手动动作；最后才允许闭环。入缝运动按 [ADR-0005](adr/0005-phd2-calibration-guided-slit-placement.md) 选择显式 authority：首选同一 G3/赤道仪拓扑的当前分级 PHD2 校准和 runtime exact-lock 分段；独立、版本化的 `G3PixelToMount` 四向标定仅作后备。两种路径都必须执行 pier-side、累计量、回程、settle 和 fresh G3 残差门。
7. 拆装、重新同轴、改变 G3 ROI/binning/方向、相机旋转或焦点域硬件后，创建新的 commissioning 版本；旧证据不得静默沿用。

## 5. 运行时顺序

```text
QHY 接受帧 + WCS + 目录目标
                │
                ├── 新鲜度/残差/run/坐标门
                ├── 发令前重算 FITS 与 WCS JSON 的 SHA-256
独立 C11 焦点 ─┤
                ├── Star Focuser Pro 当前焦位一致
配对 LED 狭缝 ─┤
                └── PHD2 新拍“配置的最短曝光”G3 FITS
                              │
                              ├── 尝试 G3 解析并保留成功或失败
                              ├── 饱和帧明确排除出焦点分析
                              └── 未饱和翼部唯一质心门
                                            │
                         选择显式 fine-motion authority
                           ├── PHD2 校准 + exact lock
                           └── versioned G3PixelToMount 后备
                                      │
                                有界闭环入缝
```

短帧解析成功时仍检查 WCS parity。解析失败时，仅当其余证据链与翼部门全部通过才允许给出目标质心。若使用 PHD2 authority，则先建立与本地 operation 绑定的 settle epoch，再令 `desiredGuideLock = guide + (runtimeSlitMidpoint - target)`；这里的 `runtimeSlitMidpoint` 是本轮 fresh 黑色物理孔径检测出的几何中点，不是反光边、固定历史坐标或有限缝段最近端点。每次 exact-lock 读回后必须再次 settle 并保存 fresh G3 帧，不能把 lock readback 当成目标已到缝。若使用独立 `G3PixelToMount` 后备，则使用同一个 fresh 中点，每个赤道仪分段仍沿用已有的 durable intent、单步/累计量、回程预留、地平线、pier side 和新鲜 G3 重拍门。详见 [ADR-0006](adr/0006-runtime-slit-midpoint-as-science-destination.md)。

重新计算 QHY 哈希发生在任何额外 G3 曝光之前。接受帧或 WCS JSON 缺失、不可读，或者当前 SHA-256 与接受/生成时记录不一致时，目标 authority 立即撤销；系统不会以只有格式正确的哈希字符串替代文件完整性验证。

## 6. 不可变证据

### 短导星曝光中的暗缝低对比度恢复（0.4.0.144）

这项恢复同时适用于较暗普通恒星，不是只给超亮星使用。若当前单帧及双侧局部背景
检查均低于暗缝门限，可用本次测量调用内最近三张不可变导星帧，在固定探测器坐标
取均值后重新检查暗缝。不得按星点配准图像，因为物理狭缝固定在探测器上。

- 必须有本轮通过的 LED 狭缝几何与轮位身份；三帧哈希、时间、曝光/ROI/binning、
  PHD2 连接/导星周期、锁点及赤道仪绑定全部有效且未变化。
- 0.4.0.145 明确区分最新单帧的 `MaximumMeasurementAgeSeconds` 与三帧跨度的
  `FreshGuidingFrameTimeoutSeconds`；例如本机分别为 5 秒和 10 秒。不能要求三张
  2 秒导星曝光在算上下载后仍全部小于 5 秒，也不能放宽最新目标残差的年龄门。
- 叠加图仍须通过原始暗缝对比度门、双侧正暗差、有效样本支持率和受限位置/角度
  搜索；任一帧削顶的像素在叠加检查中继续屏蔽。
- 目标位置、通量和导星位置只用最新原始单帧。叠加图不能提供 WCS、目标身份、
  运动成功证据或三个独立目标残差，也不能改写源 FITS。
- 沿用原有补拍上限与所有运动/回程/时间预算；不额外拍摄，不改变导星曝光、不停
  导星。融合检查仍失败时保留原有时序共识与安全暂停路径。

`phd2-fresh-slit-detector-stack` 保存每张成员帧及 mount binding、单帧和融合检测
结果与运行周期；`phd2-lock-shift-fresh-residual` 明确记录融合检测代码。
这是新鲜证据的降噪检查，不是用历史狭缝坐标跳过实时验证。

每次尝试至少产生：

- `g3-bright-target-minimum-exposure`：原始 G3 FITS、SHA-256、曝光、binning、请求增益、`focusEligible=false`；
- `plate-solve-evidence`：短帧 G3 解析成功或失败的完整证据及 SHA-256；
- `g3-bright-target-wing-centroid`：QHY FITS/WCS 哈希、目录目标、C11 焦点证据、LED 狭缝几何、所有翼部候选及拒绝原因、最终 authority/morphology gate；
- 预览叠加：目标翼部质心与实测狭缝位置；
- 后续入缝分段原有的 durable intent 与回程证据。
- fine-motion authority、PHD2 校准质量等级或四向标定版本、每段 lock/mount 实报、settle operation epoch 和 fresh target/slit 残差。

证据中明确记录 `targetSpecificConstant=null`、`opticalAxisOffset=null` 和 `focusEligible=false`，用于审计该路径没有偷用目标常量、两镜偏移或饱和帧对焦。

## 7. 代码与测试

- 纯算法：`src/UvexAdv.Observatory/BrightTargetCentroidAnalysis.cs`
- N.I.N.A. 编排：`src/UvexAdv.Nina.Plugin/RealObservationStageRunner.cs`
- Profile/UI：`UvexPluginSettings.cs`、`RealRunConfiguration.cs`、`ObservationDockable.cs`、`Templates.xaml`
- 纯算法回归：`tests/UvexAdv.Observatory.Tests/BrightTargetCentroidAnalysisTests.cs`
- 运行器安全结构回归：`tests/UvexAdv.Nina.Plugin.Tests/BrightTargetRunnerSafetyTests.cs`

这些测试不连接 PHD2、QHY、N.I.N.A. 设备或 COM5，也不产生设备动作。
