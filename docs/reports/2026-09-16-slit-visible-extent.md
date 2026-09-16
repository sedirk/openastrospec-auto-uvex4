# 狭缝照明可见长度：无运动在线验证（0.4.0.209）

## 问题与修复范围

用户指出导星预览中的青色狭缝段明显短于图像中的黑缝，并授权在云天、
硬件已经归位的状态下在线测试；明确禁止移动屋顶、镜盖和赤道仪。

旧 `SlitIlluminationPairAnalyzer.MeasureAlongSignal` 的范围被
`seed.LengthPixels / 2` 限制。当前轮位 2 的历史参考长度为 410 px，
所以该中央反光定位 ROI 不能证明完整可见范围。原叠加把这段历史中央范围
画得像完整狭缝，也没有区分入缝锚点与两端几何中点。

现在保留原反光边注册、HDR 测宽/轮位身份及入缝锚点算法，另由共享
`SlitIlluminationExtentAnalyzer` 计算照明可见段：

- 沿已注册轴在探测器内搜索，不以历史 410 px 截断；
- 使用现有短、长曝光 OFF/ON 中值合成，不增加生产序列的 18 帧曝光；
- 64 px 沿缝统计窗、8 px 采样间距、局部双侧背景扣除，5σ 信号门；
- 横向搜索限定 ±6 px，只保留连接入缝锚点的连续支持，不连接远处反光斑；
- 饱和样本无效，长档不可用时可由有效短档提供支持；缺少可见长度不改变已通过的测宽/身份门；
- 可见端点空间分辨尺度为 ±36 px，并保留触及图像边界标志。

**可见段不是物理端帽的独立测量。** 更强照明可以显露更弱末端，不能把统计阈值
位置说成精确机械端点；±36 px 也不是包含所有照明系统误差的置信区间。
`SlitGeometry.LengthPixels` 仍保留已标定中央段，`AcquisitionPoint` 不变；
新 `IlluminationExtent` 只用于叠加显示和扩大普通导星/场星匹配的排除区域。
`ClosestPointOnSlit`、精确入缝、试拍容差和运动范围不扩展。

预览使用青色虚线表示 LED 可见段，蓝色实线表示原中央段，十字仍表示入缝锚点。
底部图例明确写出可见段“非物理全长”。原 JSON 无新字段仍相容；新测量不继承旧 extent。

## 在线边界与实测

开始前通过 N.I.N.A./PHD2/UVEX 的现有只读接口确认：N.I.N.A. Cancelled、无主相机
曝光/电调焦移动；PHD2 Stopped；屋顶 ShutterClosed、平场盖 Closed、平场灯 Off；
赤道仪 AtHome、TrackingEnabled=false、Slewing=false、IsPulseGuiding=false。
UVEX 服务 Ready，显式绑定 COM3，轮位 2 / -512 步、光栅 -1923、M2 12500、LED Off。

复用 Git 忽略的 `tmp/no-motion-led-harness`，只使用 UVEX 服务租约下的 LED 命令与
PHD2 原生 `capture_single_frame`。临时工具过时的固定 COM5 检查改为显式核对
服务当前已确认绑定 COM3，未扫描、打开串口或更换所有者。无运动伪端点测试 4/4 通过。
每组都是短、长曝光各 `OFF×3 → ON×3 → OFF×3`，gain 0、1×1，共 18 张原始 FITS。

| 数据 | 曝光 | 可见起止（相对锚点，px） | 可见长度 | 原中央段 |
|---|---|---|---:|---:|
| 原截图同轮保存帧复算 | 10/20 ms | -364 → +540 | 904 px | 410 px |
| 22:24 在线组 A | 10/20 ms | -364 → +572 | 936 px | 410 px |
| 22:26 曝光对照 | 10/100 ms | -372 → +612 | 984 px | 410 px |
| 22:36 独立在线组 B | 10/20 ms | -372 → +572 | 944 px | 410 px |

两组标准曝光相差 8 px，低于端点统计窗分辨尺度；原帧复算也不再截于 410 px。
100 ms 对照显示弱尾端随照明深度改变，因此**未把正式测宽档从 20 ms 改成 100 ms**。
所有原始观测未改写；派生复算和图在本地 `output/slit-extent-20260916/`。
不能把长度检测当作新的亚分辨率缝宽标定：组 A 的既有宽度模型输出 2.5 px，
组 B 使用既有 3.5 px commissioning 转移；本次未改写宽度指纹或其不确定度。

在线原始证据根目录：`output/commissioning/2026-09-16-slit-extent/`。
三份不可变 `no-motion-led-manifest.json` 的 SHA-256：

- A：`24BE438B252E37E3D0019E0C6050E66922577CCEB6492AF11A81DB8DB65D6A91`
- 100 ms 对照：`BDD6DCA0CF3CE181D36C4752C31E25F27D536ECD0B620C414A1E06AFDA6259B0`
- B：`7785F95F39902BB4D25E65B5004899AB7D68D73BCDEA43D8F5E3DB1003B07279`

每组 finally 均回读 LED Off。盖关闭导致 `G3_FOCUS_STARS_NOT_DETECTED` 属于预期，
该离线星点指标不参与照明测试通过判定，没有为此开盖、移动或启动对焦。

## 生产路径一致性

| 验证路径 | 共享行为 | 不包含的行为 |
|---|---|---|
| 无运动在线工具 | 同一 `SlitDarkApertureHdrAnalyzer` → `SlitIlluminationExtentAnalyzer` | 不执行目标观测，不给运动授权 |
| 保存 FITS 复算 | 同一分析器、同一输入采样/饱和语义 | 不接触设备，不改原图 |
| Dockable / Advanced Sequencer | 现有同一 `RealObservationStageRunner`，通过同一 HDR 调用获得 extent | 无第二套执行引擎，无额外 LED 帧 |
| 生产预览 | 同一 `ObservationPreviewRenderer.RenderG3Bitmap`；UI harness 只合成底图像素 | 不把模拟图称为实拍 |

本轮验证的是 **真实无运动照明组件与生产共享算法**；不声称已完成改版后的前端
一键观测、真实目标入缝或科学曝光验收。屋顶/镜盖/赤道仪不在本次动作范围内。

## 发布检查

定向回归已覆盖端点超出旧 ROI、不对称延伸、斜缝、梯度、固定星点、孤立亮斑、
大缺口、饱和回退、图像边界、旧 JSON、导星排除与不扩大运动范围。

- 最终 `scripts/build.ps1`：2,152 项通过、0 失败，编译 0 警告、0 错误；
  其中 Observatory 592 项、插件 1,093 项、UI harness 99 项。
  日志：`output/slit-extent-209-final-build.log`。
- 全部 93 个 UI harness 场景渲染完成，8 张总览及新增狭缝宽/窄窗原尺寸截图已视觉复核；
  新图例与底部说明可见，窄窗可换行。输出：`output/slit-extent-209-ui/`。
- 从 `reduction/` 运行 Ruff 通过、pytest 66 项通过；没有升级科学依赖。
- 冻结设计校验通过；严格公开发布审计为 0 项文本发现、0 项缓存/未跟踪二进制数据发现；
  `git diff --check` 无空白错误。已有工作树修改保留，本轮未更改冻结设计或提交 Git。

前序失败亦保留：第一次全构建中 UI 场景名单仍期望旧 91 场景，加入两个新增场景后
完整重跑通过；一次从仓库根误运行 Python pytest 收集到 `tmp/` 第三方测试而失败，
改为规定的 `reduction/` 工作目录后 66 项通过。没有放宽生产门限处理这些检查问题。

22:53 当时待安装的 `artifacts/nina-plugin/UvexAdv.Nina.Plugin.dll` 版本 **0.4.0.209**，SHA-256：
`A8738D0863FB3EB5BD8D0FC0C3BB73B32517833EBA437F412B874DE35AF92265`。

22:53（UTC+8）最终只读复核：N.I.N.A. 仍为 **0.4.0.208 / PID 14648 / Cancelled**，
主镜对焦未运行，主相机未曝光；PHD2 `Stopped`；屋顶 `ShutterClosed`、平场盖 `Closed`、
平场灯关闭；赤道仪 `AtHome=true`，跟踪、转动、脉冲导星均为 false；主镜焦位 5000 未移动。
UVEX 仍在轮位 2 / -512、光栅 -1923、M2 12500，狭缝 LED `Off`。
本地状态证据：`output/slit-extent-209-final-status.json`。

**截至 22:53，代码、离线检查及无运动在线照明验证完成；尚未安装或重启 N.I.N.A.。**
当时安装后的真实三面板实例化检查与正式前端天空验收尚未执行，不能用离线渲染代替。
没有启动目标观测，也没有向屋顶、镜盖、赤道仪或任何调焦/光栅/切缝轴发出移动命令。

## 后续安装与软件收口（23:50–23:54，UTC+8）

用户随后明确要求软件收口、安装重启并提交云端主分支。全量构建和测试再次通过，
相同 SHA-256 的 .209 精确产物安装完成，N.I.N.A. 正常重启为 PID 30528。
自动观测、光谱检查、校准库三处实际原生面板已导航、截图并视觉复核，启动日志无
ERROR/FATAL、XAML、绑定或未处理异常发现。七个随包项目 DLL 与构建产物逐一相同。

最终为空闲、真实控制未授权、所有 N.I.N.A. 设备断开；PHD2 Stopped、UVEX LED Off。
ATR 关闭 TEC 后正常断开，旧 QHY 服务在确认无活动任务后释放相机；未重启其他所有者。
没有自动恢复观测、重新曝光或移动已归位机构。正式前端天空验收仍待完成。
备份与部署证据在本地 `output/deployment-backups/slit-extent-209-20260916/`，原生截图在
`output/target-strategy-193-native-20260916T155043Z/`（工具目录名历史保留，快照版本为 .209）。
完整范围与未完成项见 [晚间收口](../closeout-2026-09-16-evening.md)。
