# 2026-08-23 Deneb 自动观测闭环实机调试收口

**本地观测夜：** 2026-08-23（Asia/Shanghai）  
**现场条件：** 大丰近海台址，间歇性快速浅薄碎云；屋顶由操作员手动开启，平台周边约 40° 遮挡  
**结论：** “QHY 粗居中 → G3 同帧目标/导星识别 → PHD2 分段入缝与稳定导星 → N.I.N.A. ATR 10 s 曝光”技术闭环首次走通；本轮仍不是科学曝光或无人值守验收

本记录由当夜不可变 FITS、PHD2 事件审计、N.I.N.A. 图像历史和最终只读设备状态重建。原始观测仍位于 Git 忽略的 `output/commissioning/2026-08-23-night/evidence/` 和 N.I.N.A. 图像目录，没有被改名、移动或重写。本文只固化可重复的软件结论；单夜像素位置、两镜光轴差和现场放宽的 commissioning 门均不得写成机器无关常量。

## 1. 设备收口终态

2026-08-23 23:17 CST 再次只读复核，而不是把“停止测试”误写成“设备已归位”：

| 设备 | 唯一所有者 | 最终实报 |
|---|---|---|
| OnStep 赤道仪 | N.I.N.A. | `AtHome=true`、`Slewing=false`、跟踪关闭；高度约 `33.376°`、方位约 `0.193°`。驱动 Home 语义下 `AtPark=false`，因此本轮登记为 **Home**，不伪称 Park。 |
| Gemini 平场板/镜盖 | N.I.N.A. | 已连接，`CoverState=Closed`、`LightOn=false`、亮度 0。 |
| ATR585M | N.I.N.A. | 无曝光、制冷关闭、已断开。断开前执行过一次有界升温请求，但高级 API 温度仍卡在 `0 °C`，不能据此证明真实传感器温度。 |
| QHYminiCam8M | QHY service | `connected=false`，无活动 job。断开后实体滤轮位置不可实报，程序没有猜测其位置。 |
| G3M2210M | PHD2 | PHD2 进程仍可供下次使用，但 event server 实报 `Stopped`；没有导星或曝光循环。 |
| UVEX4 | `UvexAdv.Service` / COM5 | `Ready`，狭缝轮位 2，狭缝照明 LED `Off`，无错误；没有在收口时移动 M2、光栅或狭缝轮。 |
| 屋顶 | 现场既有人工系统 | N.I.N.A. dome/roof 未连接、`CanSetShutter=false`，软件无法关闭或验证屋顶。屋顶状态必须由操作员使用原控制链确认；不得把本轮软件状态当作屋顶已关闭证明。 |

## 2. 本夜实际证明的闭环

### 2.1 启动条件与 QHY 粗居中

- 赤道仪 UTC 硬门通过，实测主机/驱动时差约 1 秒量级。
- Deneb 目录目标 J2000 为 `310.3579791667°,+45.2803388889°`。
- `qhy-deneb-recenter-20260823T140555Z` 在薄云下降低星数门的显式 commissioning 条件下成功：两次 N.I.N.A. 赤道仪动作、累计实测约 `610.568 arcsec`，最终 WCS 残差 `7.547 arcsec`，最后一帧检测 144 星、FWHM 约 `3.41 px`。
- 该路径没有使用 QHY→G3 固定偏移，粗运动门与 G3/狭缝精修门保持分离。

### 2.2 两镜快速同指向解算只生成 Candidate

当 G3 获得 WCS 后立即补拍/解析 QHY，证明了用户提出的快速双解算路线可工作：

- 第一对中心约为 G3 `309.948964°,+45.500941°`、QHY `309.998658°,+45.571914°`，推导的 pierWest 单样本约为东 `+126 arcsec`、北 `+255.5 arcsec`、总量约 `285 arcsec`。
- 约一小时后的快速对中心约为 G3 `310.072680°,+45.497705°`、QHY `310.120214°,+45.556110°`，对应约东 `+120 arcsec`、北 `+210 arcsec`、总量约 `242 arcsec`。
- 两次候选在赤纬分量已有约 45 arcsec 差异，且两次曝光/解算并非严格同时。因此它们只证明 `MotionAuthority=false` 的版本化 Candidate 采集链，反而再次证明不能把某一次两镜差写成源码常量。激活预置模型仍需同安装纪元、两 pier side、温度/高度范围和多样本残差验证。

### 2.3 亮目标选星失败及同帧 ROI 修复

全画幅亮度优先的 PHD2 自动选星曾锁到 Deneb 的饱和光晕岛，操作员截图和 PHD2 星像剖面均显示它不是独立星点。PHD2 多星圆圈也不能证明这些峰属于真实独立恒星。这次失败不能通过放大容差或继续 settle 合法化。

同一张 2 s 级 G3 帧随后给出了可用的独立星点。最终采用的规则是：

1. 在 fresh G3 全幅中先用程序提取目标和候选；
2. 对超亮目标启用宽光晕保护区；
3. 只接受有限 FWHM、低椭率、低饱和覆盖、足够 SNR、离边缘和狭缝足够远的紧致源；
4. 先用 `set_lock_position(exact=false)` 让 PHD2 接受该候选；
5. 新建 guide epoch 时把 PHD2 `guide` 的后备选星限制在同一候选附近的 `80×80 px` ROI，禁止回到全画幅重新挑最亮峰。

这一区分也解释了为何毫秒级曝光没有取代普通导星路线：毫秒级只保留给“画面确实没有独立被导星”的显式退化直导目标；有普通星时仍优先使用同帧秒级曝光和独立星。

### 2.4 第一次真实 ROI 入缝闭环

证据：`phd2-deneb-roi-closed-loop-20260823T141202Z/`。

- 同帧 WCS 目标质心 `(796.946,974.721)`，狭缝采集点 `(937,440)`。
- 形态合格的独立导星候选 `(1298.884,677.789)`：SNR 约 `19.25`、FWHM 约 `4.84 px`、椭率约 `0.329`；选择 ROI 为 `80×80 px`。
- 使用 `desiredGuideLock = guide + (slit - target)`，目标 lock 为 `(1438.938,143.068)`。
- PHD2 完成 46 个、每个不超过 12 px 的 exact-lock 分段；每段都产生本地 operation-bound settle 和 fresh G3 帧。最终 lock `(1438.94,143.07)`，lock 几何残差约 `0.0028 px`。
- 独立最终 G3 WCS 把 Deneb 投影到约 `(932.006,440.627)`，相对采集点残差约 `5.033 px`，按当晚 `0.3805 arcsec/px` 约为 `1.92 arcsec`。这是本夜最强的 fresh 目标入缝验证。
- audit 中基于“最亮峰”的旧降级读数仍给出约 40.6 px，这是 Deneb 光晕/鬼影污染的反例，不能覆盖独立 WCS 的目标身份结论，也正是生产代码必须停止 brightness-only 选星的原因。

### 2.5 薄云丢锁后的重捕获与 ATR 技术曝光

证据：`phd2-deneb-reacquire-roi60-20260823T143650Z/`（目录名保留当时工具试验名；实际审计 ROI 为 `80×80 px`）。

- fresh WCS 预测目标 `(1354.044,225.829)`；在相距约 `393 px` 处选中独立星 `(914.064,353.847)`，SNR 约 `13.57`、FWHM 约 `4.11 px`、椭率约 `0.173`。
- PHD2 接受 `(920.45,355.14)`，后备搜索 ROI 为 `(880,315,80,80)`，没有再次落回 Deneb 光晕。
- 40 个有界 exact-lock 分段后最终 lock 为 `(497.02,568.02)`，几何读回残差约 `0.0014 px`。
- 碎云期间出现 3 个瞬时 `StarLost`，但同一个本地 settle operation 后续恢复 `GuideStep` 并最终 `SettleDone`；没有星点切换。由此确认 `StarLost` 不应立即销毁仍在进行、最后由 PHD2 明确裁决的 settle epoch。
- 第二张 ATR 技术曝光由 N.I.N.A. 唯一所有者路径完成：`2026-08-23_22-44-55__0.00_10.00s_0000.fits`，10 s、SHA-256 `BD27C30C9F220B8ED3854C4663F862EB52B482B21B5052DB1718BCCEC6F33DAA`。曝光前 settle 为 2 帧、0 丢帧；完整曝光期间 0 个 guide-loss 事件；曝光后仍为 `Guiding`，N.I.N.A. 记录 RMS `1.95 px / 0.75 arcsec`。
- ATR 帧存在清晰水平光谱迹线，证明“入缝—导星—N.I.N.A. 曝光”的技术路径已经形成。但 ATR API 同时报告实际 `0 °C`、目标 `-10 °C`、制冷功率约 26%，温度语义不自洽；因此该帧分类固定为 `TechnicalClosedLoopEvidenceNotScience`。

## 3. PHD2 事件和校准的新结论

- 本夜 active calibration 为 RA 方向约 `-101.3° / 16.827 px/s`、Dec 方向约 `-11.7° / 24.563 px/s`；两轴正交误差实际约 `0.4°`。这里的 `-11.7°` 是 Dec 轴在传感器上的方向角，不是“11.7° 正交误差”。
- PHD2 在真实接管中可能按不同顺序发送 `LockPositionSet`、`StarSelected`、`StartGuiding`、`SettleBegin` 和 `LoopingExposuresStopped`。如果重复位置仍在刚刚由本地程序、同帧形态门接受的候选邻域，它是接管确认，不是外部换星。
- `GuideStep` 可在一次薄云 `StarLost` 后恢复 Guiding；当前 settle 的最终权威是同一个本地 operation 对应的 `SettleDone`，而不是任何单一中间事件。
- 当前校准只有“一个 active calibration 被评估”，没有历史库择优。0.4° 本夜证据也不能追认旧安装纪元或另一 pier side。

## 4. 已落实到生产源码的改动

1. 所有五个会建立新 guide epoch 的生产入口都把形态合格候选的局部 ROI 传给 PHD2 `guide`；已有 lock 的 exact 分段和纯 re-settle 不重新选星，因而不需要 ROI。
2. 从生产 `Phd2Client` 删除会退回全画幅自动选星的试验入口。
3. `GuideStarSelector` 新增超亮目标光晕保护、紧致 FWHM、椭率、饱和覆盖、边缘、狭缝和 SNR 门；旧 API 保留供兼容，但真实 runner 传入完整目标候选以启用亮目标规则。
4. PHD2 客户端接受 ROI 并验证非负原点和正尺寸；事件状态机兼容本夜四种接管乱序，保留同 operation 的瞬时丢星 settle，并允许后续 `GuideStep` 恢复 Guiding。
5. evidence 现在明确记录 `guideSelectionRoi`、同帧形态权威和光晕排除策略，避免事后只凭 PHD2 绿圈声称“选到了星”。
6. 自动化仍保留无独立星的退化路线：使用单独 commissioning 的短曝光直接导目标、追大气抖动和狭缝上下漂移；它必须有人监督、不得作为对焦证据，也不得因无法取得普通导星而无限停住。

## 5. 尚未通过、不得扩大的结论

- 第二轮重捕获后的独立最终 G3 WCS 被云破坏，不能把其亮峰残差当作 fresh 目标入缝验收；端到端目标位置的独立通过证据来自第一轮 `5.033 px` WCS。
- ATR 温度实报尚不可信，本夜没有科学帧、科学 SNR、波长覆盖、校准灯或多小时稳定性验收。
- QHY 科学测光与 ATR 科学块尚未作为同一正式 production run 共同执行；本轮 QHY 只完成粗居中/配对证据。
- 屋顶、安全监视器和天气仍无软件权威；本轮只能登记为有人监督的技术闭环。
- 两镜快速配对的时间间隔仍有数十秒，下一步应以 runner 内 G3 WCS 成功事件直接触发 QHY 缓存复用或一次短曝光，记录曝光中点间隔，而不是用文件写入时间冒充同步。
- 80 px 是本夜经实机验证的保守“后备搜索围栏”，不是星点物理尺寸或两镜标定。未来如需适配不同 G3 ROI/binning，应把它纳入版本化 commissioning preset；不得用它放宽 PHD2 接受位置或跨越整幅光晕。

## 6. 下一晴夜最短验收

1. 安装包含本夜修复的插件，先验证真实启动条件页没有新 blocker；不重新做主镜/狭缝焦点扫描。
2. 选取高于现场 40° 遮挡并覆盖完整最坏耗时的亮目标，由 N.I.N.A. 完成目录指向和 QHY 粗 WCS 居中。
3. G3 WCS 一成功立即触发 QHY 快速配对，记录两曝光中点间隔；Candidate 只入库，不自动授权两镜预置。
4. 检查 evidence 中候选的 FWHM/椭率/光晕距离与 `80×80` ROI，然后直接运行一轮入缝、settle、fresh G3 residual。
5. 云中瞬时丢星交由同 operation settle 裁决；`SettleDone` 失败、ROI 内无星或 fresh residual 失败才进入重捕获/退化路线。
6. ATR 温度三元组（实际温度、设定点、功率）一致且到温后，再重复一张由 N.I.N.A. 保存的技术帧；之后才开始 QHY 测光与 ATR 科学块共同运行。

## 7. 关键证据索引

- `output/commissioning/2026-08-23-night/evidence/qhy-deneb-recenter-20260823T140555Z/manifest.json`
- `output/commissioning/2026-08-23-night/evidence/g3-DenebSolve/`
- `output/commissioning/2026-08-23-night/evidence/qhy-DenebPair/`
- `output/commissioning/2026-08-23-night/evidence/g3-DenebRecent/`
- `output/commissioning/2026-08-23-night/evidence/qhy-DenebFastPair/`
- `output/commissioning/2026-08-23-night/evidence/g3-Deneb/g3-deneb-roi-final-20260823T141202Z-selection.json`
- `output/commissioning/2026-08-23-night/evidence/phd2-deneb-roi-closed-loop-20260823T141202Z/audit.json`
- `output/commissioning/2026-08-23-night/evidence/phd2-deneb-reacquire-roi60-20260823T143650Z/audit.json`
- `output/commissioning/2026-08-23-night/evidence/phd2-deneb-reacquire-roi60-20260823T143650Z/atr-guided-technical-capture-repeat.json`

## 8. 收口后部署验收

操作员确认屋顶已关闭并明确授权重启后，于 2026-08-23 23:59 CST 完成部署：

- N.I.N.A. 通过正常窗口关闭流程退出，没有强制终止；
- 原子安装 `OpenAstroSpec Auto — UVEX4` `0.4.0.8`，安装目录中 7 个必需 DLL 的 SHA-256 与本轮完整构建 artifact 逐一一致；
- 新 N.I.N.A. 进程正常响应，Advanced API `2.2.15.2` 可用，插件目录清单包含 `UVEX-ADV Spectroscopy`；
- 新进程日志明确记录 `Successfully loaded plugin OpenAstroSpec Auto — UVEX4 version 0.4.0.8 by OpenAstroSpec`；
- 新日志没有 OpenAstroSpec、XAML、Binding、dispatcher、未处理异常或崩溃记录；
- 重启后 N.I.N.A. 没有自动重连赤道仪、ATR585M 或平场板，没有曝光、导星或设备运动。

这证明源码、构建 artifact 与本机安装版本一致，并通过了启动加载门；亮目标 ROI 修复的下一次真实天空回归仍须留到下一晴夜，不能用“插件加载成功”替代闭环天区验证。
