# N.I.N.A. 插件 UI 发布检查

本规则适用于每一次 N.I.N.A. 插件 XAML、Dockable ViewModel 或打包产物变更。
DLL 能被插件加载器读取不等于界面模板能安全实例化；发布前必须完成以下全部检查：

1. 运行 `scripts/build.ps1`。`XamlBindingSafetyTests` 必须通过；任何位于
   `IsReadOnly=True` `TextBox` 中的 Binding 都必须显式使用 `Mode=OneWay`
   或 `Mode=OneTime`，不得依赖 `TextBox.Text` 默认的 TwoWay 模式。自动观测
   主界面的“运行概览”和“观测计划”不得包含整页 `ScrollViewer`；新增常用状态或
   目标字段时应新增/重组页签或使用紧凑分栏，不得把常用页面重新堆成长滚动表单。
   “运行概览”不得以 `Height="*"` 把 PHD2、鬼影或其他说明卡拉满剩余高度；首页只显示
   等级/模式、是否可用和下一操作，policy SHA、commissioning route、缩放、完整适用性
   与权限契约必须放在“高级设置”的折叠详情中。首页状态必须使用操作员可读的中文，
   不得直接显示 `DegradedSupervised`、`AutoIfValidElseSkip`、decision gate code 等内部枚举或证据代码。
2. 渲染 UI harness 的全部场景并人工检查截图，不得只检查编译结果。
   会共同重建 `UvexAdv.Nina.Plugin` 的插件测试与 UI harness 测试必须串行运行；
   不得让两个 `dotnet build/test` 进程同时写同一个 WPF `obj/*_wpftmp` 目录，避免
   生成的 `InitializeComponent`/命名控件文件发生竞态。
3. 安装将要交付的精确 artifact 后启动 N.I.N.A.，至少依次打开一次
   OpenAstroSpec 自动观测和 OpenAstroSpec 校准库两个面板，并在自动观测中切换到
   `实时图像 → ATR 二维/一维光谱`，确认内嵌的 ATR 单帧检查区能实例化。
4. 检查本次新建的 N.I.N.A. 日志。出现 `XamlParseException`、dispatcher
   未处理异常、Binding 异常或非预期进程退出时，本次发布失败；即使日志已经写出
   `Successfully loaded plugin` 也不得判为通过。
5. 保存安装 DLL/artifact SHA-256、N.I.N.A. 版本、进程 ID、日志路径与三面板
   实际打开结果。不得用离线截图替代真实 AvalonDock 模板实例化检查。

`UvexAdv.Nina.Plugin.Tests/XamlBindingSafetyTests.cs` 是上述第一条的自动门；
第三、四条是安装后的真实 smoke test，必须在每次交付前执行。

自动观测主界面的布局基线为八个一级标签：`运行概览 / 设备手控 / 观测计划 /
自动准备 / 实时图像 / 失败诊断 / 质量与证据 / 高级设置`。固定运行控制条始终在标签外。
`设备手控`只通过 `UvexAdv.Service` 完成 UVEX4 显式连接/断开、状态回读、四槽位切缝、狭缝灯、
M2 有界相对步进和释放 COM5；设备选择保存在当前 Profile，打开服务或面板不得自动连接，
普通断开后不得后台重连，未连接时设备动作不得隐式连接。它不得要求 Night Setup、PHD2、
QHY、WCS 或整套自动观测 commissioning，也不得绕过服务租约、限位和动作后回读。`自动准备`应把能从 N.I.N.A.
和 bindings 自动取得的值自动填写，把人工决定项做成选择器，并在字段所在卡片就地标红；
不得把完整内部校验字符串倾倒到固定页脚。常规状态、手控、计划、准备和预览不得依赖
整页滚动；动态失败详情可以在内容确实超高时滚动，质量/证据集合可以在列表内部滚动，
参数密集的高级设置允许整页滚动。站点、地平线、模拟速度和 commissioning 原始字段
属于高级设置，不得重新占据计划页。

N.I.N.A. 的“插件 → OpenAstroSpec Auto — UVEX4 → 选项”页采用四个互不混淆的标签：
`范围与连接 / ATR 提取 / M2 对焦 / 光栅锁定`。该页只配置 ATR/UVEX4 光谱工具和对应的
两个 Advanced Sequencer 闭环项，不得暗示它控制自动观测、C11/G3 对焦或科学曝光。
ATR 提取矩形必须明确标作完整图像上的软件提取而非相机硬件 ROI；M2 与光栅使用
独立 commissioning 授权；绑定后的 ATR DeviceId 只读并优先于名称回退；未配置的
浮点字段显示为空白/“未配置”，不得向用户显示内部 `NaN` 哨兵值。

ATR 相机身份绑定和单帧提取检查并入自动观测的
`实时图像 → ATR 二维/一维光谱`。插件不得再导出只有状态、绑定和一条临时曲线的
`OpenAstroSpec 光谱`占位 Dockable。自动观测 Dockable 内的 `设备手控`是完整的 UVEX4
观测前准备任务，复用 `UvexAdv.Service` 的单一 COM5 所有权；它不形成第二套串口或采集
流程。普通断开保持 `Disconnected`；释放端口给原厂软件必须显式进入服务维护模式，
两种状态都不得后台自动重连。恢复时必须由操作员点击连接并重新读取可信位置。

该检查不能由“我没有改 XAML”口头豁免：ViewModel 属性可写性、DataTemplate、资源字典
和打包内容的变化同样可能只在真实 Dockable 首次实例化时失败。每次插件交付都必须执行。
