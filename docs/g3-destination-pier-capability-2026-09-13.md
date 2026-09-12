# 目的地侧别预测的 ASCOM 兼容修复（0.4.0.190）

## 本轮实际故障

运行 `UVEX-20260912T190630Z-dc7df136c325405` 在 WCS 居中阶段报
`G3_WCS_CENTERING_RETURN_BLOCKED`。本轮第 36、53 号 WCS summary 均保存了
`G3_DESTINATION_PIER_SIDE_QUERY_FAILED`，驱动原始消息为
`DestinationSideOfPier is not implemented in this driver.`。

第一处阻断时 WCS 移动次数为零，已在起点；旧恢复分支仍转入邻场搜索。
随后一笔实际计费运动为 306.9998″，再进入 WCS 时同一查询又阻止了返回起点。
这不是 SEP、哈希或短帧位置复核失败。

本机 N.I.N.A. ASCOM 包中的 `ASCOM.NotImplementedException` 派生自
`ASCOM.DriverException`，不是 `System.NotImplementedException`。
之前的兼容分支只识别后者以及两个短类型名，漏掉 ASCOM 基类。
本次检查使用本机 N.I.N.A. 所有者路径和安装的 ASCOM 异常程序集，未打开第二个驱动实例。

## 修复

- 对实际 ASCOM 异常继承体系进行类型判断；识别标准 ASCOM NotImplemented
  HRESULT 与 COM `E_NOTIMPL`，支持透明调用包装及只有一个异常的 Aggregate。
- “驱动没有预测功能”作为明确警告继续：不伪造预测侧别相同，不更改驱动或开启驱动能力。
  当前实际侧别、即时安全门、原运动/回程预算和运动后的读回检查仍保留。
- 不凭英文错误文本放行。不吞掉断连、超时、无效坐标、一般 COM 失败，
  或同时包含功能缺失与真实故障的 Aggregate。原始类型、HRESULT 和内部异常链进入证据。
- 真正的目的地侧别预检失败会保留原始门：先按原流程处理尚存的回程义务，
  到位后直接报告该预检错误，不再误转为重新曝光/解算/邻场搜索。
  若回程自身被阻止，仍保留回程未完成状态和原账本。
- UI 对嵌套目的地侧别查询错误显示驱动原因，不再仅显示泛化 WCS 提示。
- 插件引用 N.I.N.A. 已有的 `ASCOM.Exceptions.dll`，不随插件覆盖或另装 ASCOM SDK。

## 上一版修复的本轮实机证据

本轮第 1 号 `g3-prior-return-origin-readback` 确认 0.4.0.189 的旧最终绝对起点只读核验
已经实际通过：6 次所有者读回、覆盖 5.411 秒、最大起点残差 5.0291″、
最大窗口漂移 0.8034″，输出 `G3_MOTION_PRIOR_ORIGIN_READBACK_CONFIRMED`，
并明确 `noMountCommandSent=true`。之后已进入新的 Night Setup 和 G3 流程。
这只证明上一轮旧回程核验分支，不代表本轮完整天空闭环通过。

## 回归与部署边界

回归直接构造安装版 ASCOM 异常，覆盖功能缺失、包装异常、HRESULT、真实传输故障、
已知反侧拒绝、两次发令前预测检查，以及禁止预检故障落入光学搜索的生产路径顺序。
所有者路径仍为同一个 N.I.N.A. mediator，未增加后台独有恢复路径。

本次只修复、安装和重启，不启动观测、不回零、不补发旧位移。
现有运行原始帧和运动记录不清理、不修改。真实新一轮运行与前端三个规定面板的
人工烟雾检查仍须分别验收；离线测试、UI harness 和桥快照不能替代天空闭环。

### 安装记录（2026-09-13 03:19，UTC+8）

- 冻结设计、产品布局、品牌和坐标命令检查通过；完整构建 0 警告、0 错误。
  1,776 项 .NET 测试通过，后处理 `ruff` 通过，66 项 Python 测试通过。
- 38 个 UI harness 场景已重新渲染并检查；不是实际 N.I.N.A. 三面板操作验收。
- 确认 N.I.N.A. 已退出后安装 0.4.0.190，七个部署 DLL 与构建产物一致。
  插件 SHA-256：`6B4590776C160555B81F4BD98122479AEE43C3B779720EE4D72E0A150DA8676C`。
  没有部署额外 `ASCOM.Exceptions.dll`，继续使用 N.I.N.A. 所带程序集。
- 旧插件及本轮运动记录副本保存在忽略目录
  `output/deployment-backups/pier-190-20260913-031952/`。
- 重启后的 N.I.N.A. PID 为 29188，桥快照确认版本 0.4.0.190 与上述构建哈希，
  状态 `Idle`，`uiError` 为空，真实会话控制未授权。
- 启动日志 `20260913-031953-3.2.0.9001.29188-202609.log` 确认插件成功加载，
  截至本次核对未检出 XAML/Binding/dispatcher 未处理异常。
- 安装及重启前后本轮运动记录 SHA-256 均为
  `AD7E2F597D85E1A10F8B320A606919144E9FE30D651FEBCBB724BD76BD21C848`。
  没有清账，没有补发回程或启动观测。本版真实设备预检与天空闭环仍待复测。
