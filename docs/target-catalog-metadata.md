# 星图自动识别策略：目录资料边界

“根据星图自动”读取的是**已导入目标的目录类型**，不是让星图替代导星相机的新帧，
也不是仅凭目标名称猜分支。一次导入保存目标名、目录 ID、J2000 坐标、来源、UTC、
对象类型及可用 V 星等；随后修改目标字段会清除旧资料。计划冻结时再次比对这些字段，
不匹配的旧资料不能为新目标选择特殊路线。

## 星图接口

Stellarium 的 `/api/objects/info?format=json` 返回当前选择；显式 `name` 参数可查询
其他对象，但导入功能不借此自动换目标。插件从与目标身份补充相同的一次响应读取资料，
先确认原始返回坐标与 N.I.N.A. 同次目标相符，再按来源的实际坐标约定转换。
从 0.4.0.203 起，Stellarium 的 `raJ2000/decJ2000` 不再被直接当作原始目录坐标：
读取源时刻和光行差设置，去除已加入的光行差后，目标及目录资料统一保存 J2000 轴向坐标。
“当前赤经/赤纬”不能直接填入目标的 J2000 字段。
转换来源、限制和保存帧验证见[目录坐标统一与新帧配准](catalog-coordinate-and-guiding-registration-20260915.md)。
见 [Stellarium Remote Control API](https://stellarium.org/doc/25.0/remoteControlApi.html)。

上游 `StelObject::getInfoMap` 提供 `type`、`object-type`、`vmag`；深空对象会把
`type` 替换成本地化文字，因此分类优先使用独立的英文 `object-type`，并保留原文。
恒星还可能提供 `star-type`。未知或占位星等（例如 99）保存为未知，不改成 0。
见 [StelObject 源码](https://github.com/Stellarium/stellarium/blob/master/src/core/StelObject.cpp)、
[Nebula 源码](https://github.com/Stellarium/stellarium/blob/master/src/core/modules/Nebula.cpp)、
[StarWrapper 源码](https://github.com/Stellarium/stellarium/blob/master/src/core/modules/StarWrapper.cpp)。

其他 N.I.N.A. 星图适配器提供的原生 `DSOType` 和 `Magnitude` 也可保留；缺失字段不捏造。
构图中心没有独立目录类型时保留未知，不把拖动后的取样位置误说成某颗恒星的中心。

## 分类只决定优先路线

| 星图资料 | 建议类型 | 不会做的事 |
| --- | --- | --- |
| 恒星、双星、变星 | 直接星像识别优先 | 不会选最亮伴星替代目录目标 |
| 行星状星云 | 紧致扩展源 / 目录中心 | 不要求圆形实心恒星核 |
| 类星体、BL Lac、blazar | 暗点源 / 目录 WCS | 不要求在短曝光中看到目标峰 |
| 星系、扩展星云、星团 | 计划坐标取样 / WCS | 不把附近亮结节或恒星当成目标 |
| 未知或缺失资料 | 明示未知，先尝试直接识别 | 不凭名称或星等直接宣布目标不可见 |
| 太阳系、卫星等移动目标 | 不支持固定 J2000 自动路线 | 不把太阳的 `object-type=star` 当作普通恒星 |

V 星等供显示与解释，不采用未经台站标定的统一“几等以下不可见”阈值。
实际能否使用直接星像、能否转入后备路线，仍由共享生产 runner 的有界新帧、
目标身份、WCS、旁星、运动与安全规则决定。星图分类不是观测成功证据。

当前自动分类不提供行星星历、非恒星跟踪或太阳观测路线。
分类单元测试与导入测试只证明源码行为，不代表所有对象类型均已完成实机验收。
