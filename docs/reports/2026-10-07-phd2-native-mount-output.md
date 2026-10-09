# V460 Cyg：原生赤道仪名称造成的导星无输出漏检（0.4.0.230）

## 证据与结论

运行 `UVEX-20261006T141306Z-3c56e7b6de554d7`，目标 V460 Cyg / HIP 107129。
以下时间为 UTC+8；原始 FITS、JSON、PHD2 日志及运动账本未改写。

- 截图中的 20:41:35 停放超时发生在本轮之前。22:13:06/08/09 的 OnStep
  COM6 访问拒绝使本轮先暂停；N.I.N.A. 日志确认 22:13:30 已重新连接，22:13:41 恢复。
  不能把这些历史通知等同于最后阻断的直接原因，也没有证据确定当时谁占用 COM6。
- 22:17:27 G3 获取完成；22:17:38 PHD2 开始导星。PHD2 DebugLog 随后持续记录
  `PulseGuide`、`IsPulseGuiding` 及导星速率 COM 调用失败，错误 `0x800706ba`（RPC 服务器不可用）。
  本机 PHD2 的 PulseGuide 失败弹窗被禁用，日志显示 suppressed alert；本次未改动此设置。
- 原生 GuideStep 的 `Mount` 是 **`On-Step (ASCOM)`**，不是固定字符串 `Mount`。
  第 3–5 帧请求 RA 修正分别为 −2.213、−2.256、−3.765 px，Dec 分别为
  −1.697、−1.710、−2.335 px；两轴实际报告脉冲时长均为零（JSON 中省略零时长字段）。
  GuideLog 对应的两轴时长也均为零。星点持续检出；`ErrorCode=1` 表示饱和，不是脉冲错误码。
- 首段目标残差由约 36.9 px 增至 101.168 px，22:19:23 触发一次交接重建。
  22:20:14 再次导星后仍无有效输出，22:23:30 残差达 100.490 px，停止已确认，
  最终被包装成 `PHD2_COARSE_HANDOFF_RECOVERY_EXHAUSTED`。没有发出 exact-lock 精调动作，
  没有开始 ATR 科学曝光。

可复查证据：`00057-...141748219Z-phd2-lock-shift-fresh-residual.json`、
`00228-...141923721Z-phd2-coarse-handoff-rebuild.json`、
`00567-...142330815Z-phd2-dependency-rebuild-stop-confirmed.json`，以及
`PHD2_DebugLog_2026-10-06_221738.txt` / `PHD2_GuideLog_2026-10-06_221738.txt`。

确认的物理接口故障是 **PHD2 的 ASCOM RPC / 脉冲输出失效**，不能归因为云。
N.I.N.A. 重连成功、PHD2 仍持有失效 COM 引用是与日志一致的解释，但本次没有通过
重新连接或发脉冲做硬件验证，不能宣称已修好驱动或实测恢复了导星。

## 软件改动

旧 `Phd2GuideOutputStatus.Observe` 要求 `step.Mount == "Mount"`，因此忽略了实际驱动名称。
与官方 [PHD2 事件协议](https://github.com/OpenPHDGuiding/phd2/wiki/EventMonitoring) 及
[2.6.14 原生事件序列化](https://github.com/OpenPHDGuiding/phd2/blob/v2.6.14/src/event_server.cpp)
核对后，改为精确匹配 `get_current_equipment.mount.name`：

- 必须有当前、已连接的 mount 读回；不把任意非空字符串、RPC role、AO 或其他设备当成 mount。
- 保留原生名称，不改写原始事件。外来/AO 事件不能累计或清空已建立的 mount 计数。
- 设备读回变化打断尚未确认的连续序列；同设备重复读回不打断。
- 已锁存的输出故障仍不能被新好帧、停止/启动、设备读回或 TCP 重连清除，
  只能由原有的受检设备恢复边界复位。
- 原有三帧、至少两秒、非零修正请求且零两轴输出的判据不放宽。
  已有真实脉冲、微小舍入请求、失星、缺字段和非法数值不得伪报无输出。

修复使共享生产客户端及 runner 的现有专用处理可识别本轮数据：先停止本轮导星，
尚无未结移动责任时才可经身份、安全、停止读回复核执行最多一次原生设备重连；
已有未结 exact-lock 责任则保留账本，不重连丢弃责任，不伪称物理回程。
恢复仍须重新建立场、目标和导星证据，不能重置粗定位/精调预算或循环重建。
本次维护没有实际调用设备重连，没有增加串口扫描、PHD2 重启或机械动作权限。

两个正式前端仍走相同 canonical plan、factory、runner、PHD2 client；没有增加
Dockable、Advanced Sequencer 或维护脚本专属科学路径。

## 回归与发布

`scripts/build.ps1` 完整构建、2,518 项 .NET 测试、75 项 Python 测试、Ruff、四份冻结设计校验通过。
新增 21 项回归，覆盖本轮真实名称/数值、未知/失联/歧义设备、外来/AO 交错事件、
设备变化与锁存保留、三个生产诊断 UI 场景；原有 12 类有界重连场景也改用原生名称，
验证不会调用 guide、guide_pulse、set_profile 或 capture_single_frame。
121 个离线场景全部渲染，11 张总览及新故障窄窗逐张检查，没有修改生产 XAML。

01:16:23 通过正式取消入口结束旧轮，清理日志确认 PHD2 Stopped、无测光任务、无清理失败、
未进行机械恢复；没有未结 PHD2 锁点账本。只读状态确认相机未曝光，赤道仪无转向/脉冲，
调焦器无运动；屋顶和盖板打开、赤道仪仍跟踪，维护没有更改这些物理状态。
Profile（19 文件）和旧插件（41 文件）备份至
`%LOCALAPPDATA%\UVEX-ADV\maintenance\plugin-0.4.0.230-guide-output-20261007`，
随后正常退出 N.I.N.A.。41 个安装文件与最终发布产物逐一匹配，01:19:07（UTC+8）
正常启动 N.I.N.A. PID 24744；后台桥及 `20261007-011908-3.2.0.9001.24744-202610.log`
确认加载 0.4.0.230。插件 SHA-256：
`389A1D449D37897F79A85D47A2B33307833157CD25CBCE7F11420647CEF6B996`；
PHD2 客户端 DLL SHA-256：
`318FFED0CCA7D411AA149284113BE78B2D2DD48DD26931E27929AA30BC303FBD`。

通过 computer-use 检查实际自动观测页、校准库和 ATR 图像页，未见新增 XAML、绑定
或 dispatcher 未处理异常。实际 ATR 手动单帧完整展开区**未完成验收**：新一轮自动
观测已占用光谱相机，此区被正常禁用。没有为界面验收取消新轮或强行取得相机；
该完整展开区的离线渲染已检查，不替代空闲时的实际展开检查。

重启后最初为 Idle；验收期间 01:20:41 出现新轮并被 `HORIZON_BLOCKED` 拦在
Night Setup，01:23:16 又出现 `UVEX-20261006T172316Z-ad91bd95da12405`（V863 Cas），
随后自动推进至入缝。维护端重启后只调用 show-workflow、show-calibration-library、
show-atr-inspector 等界面导航，没有授权/启动/恢复这两轮，也没有中断它们。
因此不能将最终状态记录成 Idle，或把这轮外部发起的实天进展计作维护验收。

新轮连接/运行日志另有屋顶驱动返回非法 shutter state 5、ATR 设置温度 1000 失败、
PHD2 settle 超时。它们不是启动 XAML/绑定失败；本次维护未更改相应硬件或参数，
也不据此声称设备故障已经全部消除。

本次为软件修复和发布检查，不是前端单次启动到 FinalizeObservation 的实天验收。
