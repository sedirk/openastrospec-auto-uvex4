# 0.4.0.218 — 跨 N.I.N.A. 重启的 PHD2 校准时间

## 现场事实

`.217` 的 `UVEX-20261002T130042Z-5495eaaf77ea473` 在 21:04:50（UTC+8）
进入 `PHD2_LOCK_RECOVERY_PRE_GUIDE_CALIBRATION_INVALID`。
技术原因明确为校准年龄 `37.17:08:56.8462280` 超过 30 天，而不是新星点识别失败。
该入口在恢复 `.216` 留下的旧锁点账本，不能重复校准或清空预算来通过检查。

本轮 fresh G3 已通过并复核 mount binding；导星选星帧为 `00075`。
当前 PHD2 RPC 回读 RA angle −132.7°、RA rate 16.325 px/s、Dec angle 4.8°、
Dec rate 18.106 px/s、parity +/+、校准赤纬 35.5354°，与今晚原生校准相符。
旧账本保留了已用 2 次动作、累计 24.9946 px，以及未完成 settle 验证的回程。
这些账本数值没有被修改。

`.217` 的时间证明只活在 runner 内存中。N.I.N.A. 重启后，该证明消失，
检查回退到安装绑定包的旧日期；PHD2 本身却没有重启，仍持有今晚的新校准。
直接读注册表也不成立：PHD2 会话中配置尚未 flush，注册表仍是旧值。

## 修复

采用 PHD2 自带 `export_config_settings` 读取当前进程的设置快照，而非另造持久化账本。
只生成 PHD2 原生诊断导出，不导入配置、不改注册表、不启动曝光/校准/导星。
现场接口只读核实得到 **2026-10-02 20:33:51（UTC+8）** 的 `orig_timestamp`，
与 PHD2 debug log 相符；不是安装时间，也不是导出文件修改时间。

来源：[PHD2 EventMonitoring](https://github.com/OpenPHDGuiding/phd2/wiki/EventMonitoring)，
以及本地上游 `event_server.cpp::export_config_settings/get_calibration_data`、
`phdconfig.cpp::SaveAll`、`mount.cpp::SetCalibration/SaveCalibrationDetails`。
`timestamp` 可被翻转等 SetCalibration 操作刷新，故只能使用 `orig_timestamp`。

- 两个正式前端继续共用 `RealObservationStageRunner` 的校准 requirement。
- 本轮亲历且同连接/同校准的证明仍可用；重启或证明不匹配时，通过本机 PHD2 导出读取年龄。
- 将导出 Profile 名、RA/Dec 角度、Dec 速率、奇偶性、校准赤纬与 fresh RPC 比较。
  容差只覆盖上游 `%g` 与 RPC 小数位舍入；RA 当前速率的赤纬补偿不当作校准更换，
  但活动速率仍完整经过质量门。导出 base RA/Dec 速率必须为正且有限。
- 导出前后 Profile、连接 epoch、校准变化序列不一致则拒绝；非本机端点、路径异常、
  缺失/重复字段、无法解析或夏令时歧义的时间均不刷新年龄。未来/过期仍拒绝。
- 保存校准时间和来源；恢复入口额外写入结构化校准回读证据，再决定是否可以启动导星。
- 不改变 30 天门、不伪造 `UtcNow`、不恢复历史 settle、不移除原回程账本、不自动重试。

完整导出可能包含机器本地配置，不进入 Git；报告只保留必要校准数值。

## 验证与发布

验证已完成：

- 2,306 项 .NET 测试通过，0 警告/错误。新增 24 项原生年龄测试和 1 项生产入口回归，
  覆盖缺失/错误时间、过期/未来、Profile/校准变化、活动速率/正交性不合格及重启后的新客户端。
  原有 17 项运行期校准年龄回归继续通过。
- 冻结设计 4 文件校验、Ruff、75 项 pytest 通过；107 个 UI 场景渲染成功，
  与 `.217` 已检查截图逐文件 SHA-256 一致，无 UI 变更。
- 现场以本次构建的 `Phd2Client.ValidateCalibrationAsync` 执行只读核实：
  `CalibrationTimestampUtc=2026-10-02T12:33:51Z`，来源为 native `orig_timestamp`，
  年龄约 49 分 39 秒，不再误用 37 天的静态日期。PHD2 检查后仍 `Stopped`。
  使用现场 60° hard ceiling；正交误差仍 47.5°，并未更改或宣称优质校准。

发布已完成：

- 21:24:28 通过正式后台桥派发 `cancel`，确认 `Cancelled`；ATR 无曝光/live view，
  赤道仪无 Slew/Pulse、电调焦无移动、屋顶无运动，PHD2 Stopped，UVEX 动作全终结且灯灭，
  最新 QHY 任务均 Completed。赤道仪当时仍跟踪，屋顶和镜盖仍开；维护没有更改这些状态。
- Profile（19 文件）与旧插件（41 文件）备份于本机
  `%LocalAppData%/UVEX-ADV/maintenance/plugin-0.4.0.218-20261002-native-age/`。
- 通过 computer-use 正常退出 N.I.N.A.，日志 21:26:13 记录 shutting down，
  等进程消失后执行正式安装脚本；41 个文件与最终 artifact 全部一致。
- N.I.N.A. 3.2.0.9001 于 **21:27:38（UTC+8）** 启动，PID **14560**，实际桥读回 `.218`。
  DLL SHA-256：`8F5D4DB414BC46E49CA0600F7585EC78040A800BB275CB2DD9B9DE88F8B54B60`。
- 实际自动观测、ATR 单帧检查展开区、校准库三面板均检查，返回工作流空闲页。
  截至 21:30:32，新日志 `20261002-212739-3.2.0.9001.14560-202610.log` 的
  ERROR/FATAL/XAML/Binding/UnhandledException 匹配数为 0，后台 `uiError` 为空，
  `realControlArmedForThisNinaSession=false`。

构建/离线 UI 日志为本机 `output/native-calibration-218-build.log`、
`output/native-calibration-218-ui.log`。没有提交原始数据、机器配置或这些生成产物。

此变更只修复时间来源；当前校准的正交性仍由原质量策略评级，
不能把年龄核验通过称为精确入缝或科学观测成功。没有启动新观测、恢复运行或发机械动作，
没有重启 PHD2/服务，没有清除旧账本；正式前端恢复到完整科学采集仍待实天验收。

### 安装检查期间的新运行

最终只读复核发现前台于 21:30:49 新建了
`UVEX-20261002T133049Z-0ff682610b4a469`，21:32 左右为 `RunningAuto`、
真实模式、G3 解算/居中阶段。维护只派发了取消旧运行和四次只读页面导航，
没有派发新运行的启动、授权或恢复命令，也没有中断该新运行。
因此上面的 Idle/设备断开仅描述安装后的检查时点，不是此后仍然空闲的保证；
新运行由生产流程控制设备，其结果独立记录，不能提前当作修复的整轮成功验收。
