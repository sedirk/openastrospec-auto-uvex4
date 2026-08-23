# ATR585M 制冷会话有界恢复

本流程只处理当前已绑定的 ATR585M 在 N.I.N.A. 会话中出现的遥测/制冷失效。它不是通用相机重置脚本，也不允许绕过设备所有权：ATR585M 始终只由 N.I.N.A. 打开，脚本只调用本机 N.I.N.A. Advanced API，不加载 ToupTek SDK。

当前已审计组合为：

- N.I.N.A. `3.2.0.9001`
- Advanced API `2.2.15.2`
- API `http://127.0.0.1:1888/v2/api`

活动 Profile GUID 和 ATR585M 的完整稳定 `CameraId` 属于机器本地配置，因此没有写进源码或本文档，也没有默认值。每次运行都必须显式传入这两个精确身份；脚本会同时与活动 Profile、当前相机及 N.I.N.A. chooser 做全等比较。任何一项不相等，执行路径都会 fail closed。版本升级后，应先重新核对 API 路由语义和相机驱动行为，再修改工具。

## 官方温控语义与默认策略

图谱 [ATR585M 官方手册](https://www.touptek-astro.com/dl_manual/ATR585M_en.pdf)
把相机描述为双级 TEC、可控风扇和 PID 直接调节到目标温度（标称调节偏差约
`0.1 °C`），没有要求按分钟阶梯升温或降温。于是本项目把下面两件事严格区分：

- **科学温度稳定：** 直接设定目标温度后，必须用连续真实温度、实际 set point、
  `CoolerOn` 和功率/趋势证明到温；
- **设备保护动作：** ATR585M 默认不需要人为构造的多分钟温度斜坡。收口时确认空闲、
  关闭 TEC、由 N.I.N.A. 正常断开并确认 cooler off。

若未来厂商为特定硬件修订、驱动或本台实测提出斜坡要求，应把它添加为显式机型策略，
同时记录来源、版本和 commissioning 证据；不得把经验习惯当作本机默认。本文恢复脚本的
`0 min` 直接制冷正是该策略，15 分钟只是等待真实温度稳定的上限，不是降温斜坡。

## 为什么不能相信 `AtTargetTemp`

Advanced API 2.2.15.2 的 `AtTargetTemp` 是用当前 `Temperature == TemperatureSetPoint` 计算的。当两个失效读数同时为 `0` 时，它会返回 `true`；Profile 中缓存的 `TargetTemp=-10` 也不能证明相机实际收到了这个设定值。

恢复验收只使用：

1. 实际 `TemperatureSetPoint` 是否为 `-10 ± 0.2 °C`；
2. 非零制冷功率和朝向目标的真实温度趋势；
3. 最后三次温度是否都在 `-10 ± 0.5 °C`。

`AtTargetTemp` 会写入审计，但永远不参与通过判定。

## 只读预检

可以在不动作设备的情况下随时运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\recover-atr585m-cooling.ps1 `
  -ExpectedProfileId '<active-profile-guid>' `
  -ExpectedCameraId '<complete-stable-ATR585M-camera-id>'
```

预检会核对：

- 冻结设计文件的哈希；
- 唯一 N.I.N.A. 进程、版本及唯一可见的 `toupcam.dll` 所有者；
- 没有运行 ToupSky、SharpCap 等已知的第二相机宿主；
- 活动 Profile 与当前连接相机都精确匹配 ATR585M；
- N.I.N.A. 高级序列未运行、相机未曝光、未开 Live View；
- 三次真实遥测样本。

只读预检即使全部通过，也会输出 `scienceAllowed=false`，因为它没有修复或证明制冷。

## 有界执行

只有在调焦扫描或其他相机任务已经明确结束后，才可运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\recover-atr585m-cooling.ps1 `
  -ExpectedProfileId '<active-profile-guid>' `
  -ExpectedCameraId '<complete-stable-ATR585M-camera-id>' `
  -Execute `
  -ConfirmFocusScanComplete `
  -AuthorizationPhrase FOCUS-SCAN-COMPLETE-ATR585M-EXACT-OWNER
```

三个执行控制缺少任意一个，脚本都会在设备动作前拒绝。完整执行严格限制为：

1. 再做三次紧邻动作的 N.I.N.A. 空闲检查；
2. 通过 N.I.N.A. 断开 ATR585M 一次；
3. 在断开状态再次核对唯一 SDK 所有者、活动 Profile、chooser 的完整稳定 `DeviceId` 和序列状态；
4. 用完整稳定 `DeviceId` 通过 N.I.N.A. 重连一次；
5. 重连后采样三次遥测；
6. 通过 N.I.N.A. 直接下发 `-10 °C`、`0 min` 一次；
7. 最多监测 15 分钟并执行上述真实遥测验收。

断开、重连或制冷请求都没有自动重试。这样可以避免在身份、会话或设备状态异常时重复作用相机。

## 从“断开已成功”状态续跑

如果一个执行 run 已经成功断开 ATR585M，但在读取断开态 `/equipment/camera/info` 时因为该响应省略 `DeviceId` 而失败，禁止重新运行普通恢复——普通恢复会尝试第二次断开。应使用显式 continuation 模式，并提供原 run 的两个预先核对过的 SHA-256：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\recover-atr585m-cooling.ps1 `
  -ExpectedProfileId '<active-profile-guid>' `
  -ExpectedCameraId '<complete-stable-ATR585M-camera-id>' `
  -Execute `
  -ConfirmFocusScanComplete `
  -AuthorizationPhrase FOCUS-SCAN-COMPLETE-ATR585M-EXACT-OWNER `
  -ResumeAfterDisconnectedRun 'C:\absolute\path\to\prior-run' `
  -ExpectedPriorSummarySha256 '<64-hex-summary-sha256>' `
  -ExpectedPriorAuditSha256 '<64-hex-audit-sha256>'
```

`-ResumeAfterDisconnectedRun` 只接受绝对 run 目录或其中 `summary.json` 的绝对路径。续跑会验证：

- 两个文件的显式 SHA-256，并在动作前再次复核它们没有改变；
- summary 和每一行 JSONL 的格式、runId、路径和身份；
- 原 run 为未完成且科学门关闭的执行 run；
- summary 和 audit 的动作计数均严格为 `disconnect=1 / connect=0 / cool=0`；
- 原 run 恰好有一次成功的 `/equipment/camera/disconnect` 响应，且没有 connect/cool 请求；
- 原 run 正是停在断开态响应省略 `DeviceId` 的已知兼容性错误；
- 当前仍是同一个 N.I.N.A. PID 和启动时间；
- 当前相机仍断开；断开态 `DeviceId` 可以省略，但如果存在，只能是精确 ATR585M ID；
- 当前唯一 SDK 所有者、Profile、chooser ID、序列、曝光和 Live View 门全部通过。

续跑的新审计会保存原 summary/audit 的绝对路径、SHA-256 和原 runId，并把累计动作计数继承为 `1/0/0`。它不会调用 disconnect；任何意外进入 disconnect 都会先把累计计数变成 2，并在发送 HTTP 前 fail closed。续跑随后只允许一次 connect 和一次 cool，最终仍必须达到累计 `1/1/1` 以及相同的真实温度验收门。

## 失败与科学数据门

以下任一情况都会非零退出，并在摘要中写入 `scienceAllowed=false`：

- Profile、版本、相机完整 ID 或 SDK 所有权不匹配；
- N.I.N.A. 正在曝光、Live View 或高级序列仍在运行；
- 断开或重连一次后未达到所需连接状态；
- 单次制冷请求后连续三次仍是 `Temperature=0 °C / CoolerPower=0%`；
- 实际设定值不是 `-10 °C`；
- 没有制冷功率/温度趋势证据；
- 15 分钟内没有连续三次达到 `-10 ± 0.5 °C`。

失败后工具不会自动继续曝光，不会再断连，不会再重连，也不会重复下发制冷。它也不会自动调用 `cool?cancel=true` 或升温：N.I.N.A. 3.2 的取消路径可能用失真的当前温度重写实际设定值，自动“清理”反而会破坏诊断状态。工具只做一次只读的失败后相机状态采样并写入审计；如果唯一一次重连本身失败，相机可能保持断开，仍不进行第二次动作。

若单次重连后仍为 `0 °C / 0%`，应停止科学采集；下一层恢复应另行明确授权，先完整退出 N.I.N.A.，再检查 USB/相机供电，而不是让第二个程序直接打开 ATR585M。

## 审计

每次运行都会在以下忽略目录建立独立 run：

```text
output/commissioning/atr585m-cooling-recovery/<run-id>/
```

其中：

- `audit.jsonl`：追加式 API 请求/响应、门禁、遥测与动作计数；
- `summary.json`：最终结果、`scienceAllowed`、三个动作计数和最后遥测；
- `nina-log-*-tail.txt`：运行前后或失败时的 N.I.N.A. 日志尾部快照。

只有 `summary.json` 同时满足 `result=ATR585M_COOLING_RECOVERED`、`scienceAllowed=true`，且动作计数恰好为 `disconnect=1 / connect=1 / cool=1`，调用方才可以继续依赖制冷的科学曝光。
