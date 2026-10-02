# 2026-10-02：PHD2 校准后的赤道仪静止交接（0.4.0.215）

## 现场证据与原因

运行 `UVEX-20261002T112352Z-9598ba990caa449` 的 `PlaceTargetOnSlit` 被
`POST_CALIBRATION_G3_REACQUISITION_BLOCKED` 包装。manifest 的内部原因实际是
`G3_SEARCH_PULSE_GUIDING_ACTIVE`，不是校准不合格，也不能据此判定为云。

- `00065-20261002T112825018Z-phd2-post-calibration-g3-reacquisition.json`
  记录当前策略接受的校准（并非严格无人值守校准验收）：轴速率 16.172 / 32.831 px/s，
  正交误差 37°；尚未发 exact-lock 命令。
- PHD2 `PHD2_DebugLog_2026-10-02_184638.txt`：19:28:25.046 收到 `stop_capture`；
  19:28:26.061 开始最后的 S 轴 1367 ms 脉冲；19:28:27.639 报
  `scope still moving after pulse duration time elapsed`，随后 Move failed。
- 19:28:27.981 PHD2 返回 Stopped，`00066-20261002T112827985Z-phd2-calibration-stop-confirmed.json`
  保存 connection epoch 1 / guide epoch 50 的停止回执。
- 紧接着的 G3 重取入口看到赤道仪仍为 IsPulseGuiding，正确拒绝此时的移动，
  但此前缺少有界的“采集停止 → 物理脉冲结束”交接等待。
- 19:33 只读复核赤道仪已非 Slewing、非 IsPulseGuiding。N.I.N.A. 日志还记录
  2 秒设备轮询超时；无法仅凭这些记录区分脉冲尾部与缓存延迟各占多少，不能掩盖驱动报错。

## 修复边界

在同一生产 runner 的强制校准后停止回执与 G3 重取之间加入只读交接等待：

1. 持续复核原 PHD2 停止会话与连接/导星纪元；重连、重新开始导星或暂停均不继承停止证明。
2. 只对 `G3_SEARCH_PULSE_GUIDING_ACTIVE` 作短期等待；连续 3 秒通过原赤道仪状态门
   才交接，最多 15 秒。断连、转向、停止跟踪、停放、侧别变化等立即退出。
3. 等待包含 RPC 截止与取消，不发停止之外的新设备命令、不采集、不重启、不重复校准。
   原粗定位/精调账本和起始时间不重置；交接成功仍须走原新帧、目标、狭缝与运动许可验证。
4. 保存 `phd2-post-calibration-mount-idle` 逐样本时间/代码与等待结论；超时显示
   `PHD2_STOP_MOUNT_IDLE_TIMEOUT`，不能把 PHD2 Stopped 等同于赤道仪静止。
5. 原包装错误在界面展示“校准已通过，重新获取目标受阻”及内部原因；保留内部质量指标，
   不再误导用户重复校准。

Dockable 与原生目标容器仍共用同一 `RealObservationStageRunner`；没有另建测试专用硬件路径。
确定性回放覆盖脉冲尾部、缓存等待、再次出现脉冲、永久活动、迟到读回、其他状态失败及取消。
真实天空从正式前端单次启动到完成仍待验收；本维护请求不授权曝光或运动。

## 发布检查

- `scripts/build.ps1` 全量成功，0 警告、0 错误，2,251 项 .NET 测试通过
  （含 1,177 项插件、110 项 UI harness）；针对性 126 项先行通过。
- 四份冻结设计校验通过；固定 Python 环境 Ruff 及 75 项 pytest 通过。
- 105 个生产 XAML 场景完成渲染；103 个既有场景与 .214 图像哈希相同，
  两个新增正常/窄窗故障场景逐图复核，真实原因、建议及代码可读。
- 构建日志 `output/post-calibration-215-build.log`、专项日志
  `output/post-calibration-215-focused.log`；UI `tmp/ui-screenshots-post-calibration-215/`。

## 安装与实际界面验收

19:44 使用现有后台桥的前台 `cancel` ICommand 取消阻断运行，终态 Cancelled，
没有发出机械收口命令。PHD2 原生日志确认 Stopped；N.I.N.A. 主相机无曝光/live view、
赤道仪无 slew/pulse、调焦器无移动/稳定过程、屋顶无移动；UVEX Ready，QHY 作业 Completed。
N.I.N.A. 的旧 guider UI 缓存仍显示 LostLock，因此停止证明采用 PHD2 本次 RPC 回执，
没有把那个旧标签当成新状态。

备份 `%LocalAppData%/UVEX-ADV/maintenance/plugin-0.4.0.215-20261002-post-calibration/`，
包含 19 个 Profile 文件与 41 个旧插件文件。19:47 正常关闭 N.I.N.A.，确认进程退出后
执行正式安装脚本预检及安装，41 个文件逐一与 artifact 校验一致，未强杀设备所有者。

- N.I.N.A. 3.2.0.9001，PID 11624，启动 2026-10-02 19:47:54（UTC+8）。
- 安装及后台实际加载版本 **0.4.0.215**；DLL SHA-256
  `96F5A78112946B017D11824B11EC50BB24CB6C890359BCE42F04F5C63B0D0FE4`。
- computer-use 实际打开自动观测、校准库、光谱相机预览与展开的手动单帧检查区，
  均正常实例化；未点击采集、连接、绑定或执行按钮。最后返回运行概览。
- 初次启动与三个面板检查时后台 Idle、真实控制未授权、uiError 为空，无错误。
  随后 19:50:26 现场前端另开 `UVEX-20261002T115026Z-8b8fdce8acc84e9`，
  19:51 只读快照已为 RunningAuto / G3 解算。本维护只调用四次导航命令，未调用启动、
  恢复或真实授权命令，也未打断新运行。
- 本次日志 `20261002-194755-3.2.0.9001.11624-202610.log` 在新运行连接设备后出现
  两条 ERROR：19:50:30 屋顶驱动返回不合规 shutter state 5；19:50:32 ToupTek 初始化
  温度设定值 1000 失败。未见 XAML、Binding、未处理异常或进程退出，不能将整份日志说成零错误。

本维护未重启 PHD2/Windows 服务、未命令移动赤道仪/调焦器/屋顶/盖板、未发曝光或自动续拍；
后续现场前端新运行的设备动作不属于本维护验收。
原 FITS 和历史日志保留；本轮不提交 Git、不推送。只读等待回放及面板验收不能证明
On-Step 驱动脉冲异常已在真实天空消失，也不能替代从正式前端启动的整轮入缝/科学验收。
