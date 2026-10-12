# HD 21641：原生选星完成前的锁点交接竞态

范围：2026-10-12 03:08–03:44（UTC+8）的最后一次原生序列，安装版 .284。
维护版本：0.4.0.285。共享 PHD2 客户端与生产 runner 修正，不增加第二条观测路线，
不修改冻结设计、曝光/运动预算或原始观测。不自动开始或恢复观测。

## 事实与根因

- 第一目标 HD 21455，`UVEX-20261011T190811Z-8c7a4b56c1e740b`：
  `Completed / RUN_COMPLETED`，接受 2 × 600 s、累计 1200 s，其中一帧试拍复用。
  这是 .284 的真实原生运行结果，不是维护测试生成的数据。
- 第二目标 HD 21641，`UVEX-20261011T193507Z-2ad996c111a046f`：
  取场通过、双短帧分散约 0.99 px；旁星候选在 SNR 采样中丢失，三次原生选择后
  进入已配置并授权的直导目标回退。没有发出 guide，也没有 ATR 光谱曝光。
- 03:44:13.038 本地请求 `set_lock_position(exact=false)`；PHD2 在当前帧找到
  `(812.91, 402.44)`，立即 RPC 回读就是该点。下一张循环帧完成原生
  `SELECTING → SELECTED`，将锁点更新为 `(811.03, 402.05)`，差约 1.92 px，
  原生 SNR 44.7。03:44:13.405 最后回读已经是这个完成后的锁点。
- 客户端过早把即时 RPC 回读作为最终选星结果；生产 runner 的严格所有权比较
  因差值大于 0.25 px 返回 `PHD2_PRE_GUIDE_OWNER_CHANGED`。该代码本来用于
  外部会话变化，属于整轮停止，因此没有采用质量失败跳目标。停止清理无错误。
  不是截止、天气、3 px 精度警告，也不是电子限位导致本次停止。
- 原生夜间记录为 Terminal；独立安全待机继续保护，到 05:01:29 确认
  `Closed / NIGHT_DEADLINE_REACHED`，错误列表为空。白天维护前只读状态再次确认
  屋顶 Closed、赤道仪 AtPark/无跟踪/无 Slew/无导星脉冲、PHD2 Stopped。

原始依据：该 run 的 00094/00096 原生旁星证据、00097 回退、00099 交接，
PHD2 `PHD2_DebugLog_2026-10-11_190352.txt` 03:44:13 段，以及对应 night/standby JSON。
与 [PHD2 非精确选星 RPC](https://github.com/OpenPHDGuiding/phd2/blob/master/src/event_server.cpp)
及 [Guider 原生状态机](https://github.com/OpenPHDGuiding/phd2/blob/master/src/guider.cpp)
的 `UpdateGuideState` 分支相符：即时设置与下一循环帧完成选星是两个时点。

## 修复边界

1. `SelectGuideStarAsync` 仍只发送一次原来的非精确选星命令。若起点是 Looping，
   在原有有限帧等待时限内等待本次 `LockPositionSet / StarSelected`，再读取 Selected
   和最终锁点。不发送额外 loop、曝光、guide、校准或机械动作，不重新 WCS/找缝。
   已处于 Selected 的原生状态无需重复等待首次选择；停止态兼容调用保持原行为。
2. 同一命令期间保留连接、设备/Profile、校准、暂停和事件检查。只接受原有选星
   搜索半径内的一次完成，事件与最终锁点仍按 0.25 px 核对；后续严格的
   `CanRecordPreGuideOwner` 未放宽。额外重选、无配对/远端锁点、导星、校准、
   dither、停止、断连、配置变化仍不能被收养为本地选择。
3. 联动修正原生 `Selected` 的预览语义：它和 `LoopingExposures` 表示同一仍在采集的
   选星预览；本地 guide RPC 可以消费该预览的一次结束通知，不应因此丢掉它自己的
   SettleDone。非本地/已完成 guide 的停止事件不获此例外。
4. 仅本地预览候选丢失或未完成选择的有限超时使用专用类型。入缝生产边界确认停止后
   保留 `PHD2_PRE_GUIDE_SELECTION_UNAVAILABLE` 目标质量结果，原生保存策略可以跳下一个。
   停止未确认仍停止整轮；外部控制变化、取消、天气 Unsafe 不改成质量失败。
5. 真正的导星前所有权拒绝增加选星点、回读锁点与会话快照证据。两种前端都仍走
   同一个 runner/客户端；没有全局放宽残差门，也没有把锁点等同于目标入缝证明。

## 回归与发布

- 最终完整 `scripts/build.ps1`：3,407 项 .NET 通过，编译零警告、零错误；75 项 Python、
  Ruff、四份冻结哈希和严格公开发布审计通过。最终日志 `tmp/build-0285-release.log`。
- PHD2 客户端 370 项测试覆盖选星完成早于/晚于 RPC 回复、坐标不变时原生不重复发锁点
  事件，以及重选、dither、暂停、校准、配置变化、断连、丢候选、有限超时和取消。
  预览接管测试真正发出模拟 guide 并等待同操作 SettleDone，覆盖两种事件先后顺序。
- 原生容器专项 100 项通过，新增质量失败后下一目标真正完成、停止失败不继续、
  显式 EndNight 不继续、真实 `PHD2_PRE_GUIDE_OWNER_CHANGED` 不可当作质量跳过。
  原来严格所有权比较的拒绝用例保留，没有把它的 0.25 px 改成更大的值。
- 9 份历史成功/部分成功记录、26 张已接受光谱的决策及输入散列只读对照通过，
  结果在 `tmp/productive-replay-0285-release.json`；不是全部原始 FITS 重新处理。
- 开发中首轮旧模拟协议脚本未提供新增只读回读，修正夹具后发现 Selected 预览结束
  会破坏本地 guide 的操作凭证，已修复并做全测。追加“相同锁点不发事件”用例的
  首次初始化夹具也曾失败，改为先建立明确预览状态后通过；失败日志保留，最终全测重跑。

- 最终 166 个 UI harness 场景渲染，与 .284 PNG 全部逐字节相同；直导目标页重新查看。
  离线可选星历/NOVAS/SOFA/SqlClient 提示保留，渲染退出码为 0。
- 原夜间与独立待机均已终结、无停止错误；维护前再次确认屋顶 Closed、AtPark、
  跟踪/运动/采集停止及无活动 canonical 运动句柄。此时白天 AIWeather Unsafe，
  没有尝试开顶或观测。备份 Profile/.284 插件后正常退出 PID 17444，未强杀。
- 10:37:47 正常重启 N.I.N.A. 3.2.0.9001，PID 9416；41 个发布文件全部匹配。
  前端桥和启动日志确认 .285，主插件 SHA-256：
  `BA385E67F8A762243016614536BE288011CAAFAA753CF7C1E011333B196EC316`；
  本次 PHD2 客户端 DLL SHA-256：
  `BCE0557FB386D3E674BEECFC2C134C8492AC4241695E14FED065964359F15C85`。
- 三次现有前端导航完成，ATR 页签/手动区状态设置成功、`uiError` 为空；自动观测与
  校准库报告 `PanelNotAvailable`，因此**实际面板可视覆盖未验证**。没有使用
  computer-use，没有把内部选中状态当成面板可见。新进程日志未见 ERROR/FATAL、
  XAML、绑定或 dispatcher 未处理异常。
- 最终 Idle、模拟选中、真实控制未授权，N.I.N.A. 设备未重连，无新观测或自动续拍。
  PHD2 PID 23692、UVEX 服务 4856、QHY 服务 4848 保持原进程，没有额外机械动作。
  旧独立待机不随重启恢复；此次屋顶已在重启前确认关闭，不依赖重启后的软件监控。

本地备份：`%LOCALAPPDATA%/UVEX-ADV/maintenance/plugin-0.4.0.285-20261012T023744Z/`。
新日志：`%LOCALAPPDATA%/NINA/Logs/20261012-103748-3.2.0.9001.9416-202610.log`。
最终 UI 输出：`tmp/ui-0285-release/`；原始 FITS、manifest 和日志只读保留。
软件测试及安装不等于 HD 21641 已在真实天空恢复成功，须下次授权的正常序列验收。
