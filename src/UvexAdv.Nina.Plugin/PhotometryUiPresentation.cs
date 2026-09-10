using System.Globalization;
using UvexAdv.Qhy.Core;

namespace UvexAdv.Nina.Plugin;

/// <summary>Operator wording only. Never used to authorize a job or alter its state.</summary>
internal static class PhotometryUiPresentation
{
    internal static string JobKind(QhyJobKind kind, CultureInfo? culture = null) => kind switch
    {
        QhyJobKind.Acquisition => T("广域定位（光谱优先）", "Wide-field acquisition (spectroscopy priority)", culture),
        QhyJobKind.Photometry => T("连续测光", "Time-series photometry", culture),
        _ => T("未知任务类型", "Unknown job type", culture),
    };

    internal static string JobState(QhyJobState state, CultureInfo? culture = null) => state switch
    {
        QhyJobState.Queued => T("等待开始", "Queued", culture),
        QhyJobState.Running => T("正在执行", "Running", culture),
        QhyJobState.Pausing => T("正在暂停，等待当前帧与设备动作结束", "Pausing; waiting for the current frame and device actions", culture),
        QhyJobState.Paused => T("已暂停，不会开始下一帧", "Paused; no new frame will start", culture),
        QhyJobState.PausedNeedsAttention => T("已暂停，需要检查", "Paused; attention needed", culture),
        QhyJobState.Cancelling => T("正在停止，尚未确认结束", "Stopping; completion not yet confirmed", culture),
        QhyJobState.Cancelled => T("已停止", "Stopped", culture),
        QhyJobState.Completed => T("任务已完成", "Job completed", culture),
        QhyJobState.Faulted => T("任务出错，不能自动继续", "Job failed; automatic continuation is unavailable", culture),
        QhyJobState.TakenOver => T("已由操作员接管", "Taken over by the operator", culture),
        _ => T("状态未知，请检查技术详情", "Unknown state; inspect technical details", culture),
    };

    internal static bool IsActive(QhyJobSnapshot? job) => job is not null &&
        !PhotometryWorkerHost.IsTerminal(job.State);

    internal static string JobSummary(QhyJobSnapshot? job, CultureInfo? culture = null)
    {
        if (job is null) return T("尚未收到任务。开始接收后，请回到光谱主控启动观测。", "No job received. After enabling reception, start observing from the spectroscopy master.", culture);
        return $"{job.RequestedTarget} · {JobKind(job.Kind, culture)}\n{JobState(job.State, culture)} · " +
            T($"已记录 {job.TotalFrameCount} 帧", $"{job.TotalFrameCount} frames recorded", culture);
    }

    internal static string JobAdvice(QhyJobSnapshot? job, bool operatorPaused, CultureInfo? culture = null)
    {
        if (operatorPaused) return IsActive(job)
            ? T("人工暂停/停止仍有效。需要重新测光时，先停止当前任务；确认结束后，再点击“允许接收新任务”。", "Operator pause/stop remains in effect. To start again, stop the current job, wait for its terminal state, then allow new jobs.", culture)
            : T("人工停止仍有效。点击“允许接收新任务”后，由光谱主控决定何时发起新任务；不会恢复旧任务。", "Operator stop remains in effect. Allow new jobs, then let the spectroscopy master schedule a new one; the old job is not resumed.", culture);
        if (job?.YieldedToAcquisitionRequestId is not null) return T("测光已为光谱定位让路。已有图像保留，定位和导星恢复后由主控安排续测。", "Photometry yielded to spectroscopy acquisition. Images are retained; the master schedules continuation after acquisition and guiding recover.", culture);
        if (job?.State is QhyJobState.Faulted or QhyJobState.PausedNeedsAttention)
            return Error(job.AttentionReason ?? job.Error ?? "", culture);
        return T("光谱主控负责目标、定位和观测进度；测光端不会独立转向或控制导星。", "The spectroscopy master owns targets, acquisition and observing progress; the photometry instance never slews or controls guiding independently.", culture);
    }

    internal static string EndpointSummary(string? endpoint, CultureInfo? culture = null)
    {
        if (NinaInstancePolicy.TryWorkerEndpoint(endpoint, out _))
            return T("已保存测光端地址；尚未验证连接。实际启动时会核对另一实例及设备身份。", "Photometry address saved; connection is not yet verified. The peer instance and device identities are checked when the run starts.", culture);
        if (IsLegacyEndpoint(endpoint))
            return T("当前仍使用旧版独立采集服务，尚未切换双 N.I.N.A.。要切换，请先在空闲时释放旧服务，再粘贴测光端地址。", "The legacy acquisition service is still selected, not dual N.I.N.A. To migrate, release the old service while idle, then paste the photometry instance address.", culture);
        return T("尚未配置有效地址。请从另一 N.I.N.A. 的测光协同页复制地址，不要填写相机型号。", "No valid address is configured. Copy it from the other N.I.N.A. instance's photometry page; do not enter a camera model.", culture);
    }

    internal static bool IsLegacyEndpoint(string? address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);

    internal static bool IsDifferentProfile(string? master, string worker) =>
        Guid.TryParse(master, out var id) && id != Guid.Empty && Guid.TryParse(worker, out var own) && id != own;

    internal static string Error(string raw, CultureInfo? culture = null)
    {
        var code = raw.Split(':', 2)[0].Trim();
        var known = code switch
        {
            "PHOTOMETRY_ENDPOINT_INVALID" => T("地址未保存。请复制另一个测光实例的完整 nina:// 地址；不能指向当前光谱实例。旧版服务请保留有效的 http(s) 地址。", "Address not saved. Copy the other photometry instance's full nina:// address; it must not point to this spectroscopy instance. A legacy service needs a valid http(s) address.", culture),
            "PHOTOMETRY_CAMERA_BUSY" or "PHOTOMETRY_BUSY" => T("测光相机或接收端正被其他任务占用。请检查本实例的拍摄和高级序列，等当前任务结束；不要在光谱主控中再次连接同一相机。", "The photometry camera or receiver is busy with another job. Check this instance's imaging and Advanced Sequencer, then wait for that job to finish. Do not open the same camera in the spectroscopy master.", culture),
            "PHOTOMETRY_CAMERA_CHANGED" or "PHOTOMETRY_CAMERA_BINDING_MISMATCH" or "PHOTOMETRY_ACCESSORY_CHANGED" => T("实际设备与已绑定身份不一致。请停止后核对相机、滤镜轮和电调焦的选择；不要用型号相似的设备替代原绑定。", "Actual equipment differs from the binding. Stop and check camera, filter-wheel and focuser selections; a similar model cannot substitute for the bound device.", culture),
            "PHOTOMETRY_FOCUS_PENDING" or "PHOTOMETRY_FOCUS_STILL_MOVING" => T("测光电调焦的上一次运动还未确认完成。请检查原生位置与运动状态；未确认前不会换滤镜或开新曝光，也不能删除记录来继续。", "The last photometry-focus move is not confirmed complete. Check native position and motion status. No filter change or new exposure is allowed until confirmed; deleting records is not a recovery.", culture),
            "PHOTOMETRY_FOCUS_POLICY_REQUIRED" or "PHOTOMETRY_FOCUS_POLICY_INVALID" or "PHOTOMETRY_FOCUS_POLICY_CHANGED" or "PHOTOMETRY_NIGHT_SETUP_MISSING" => T("本夜配置缺少匹配的测光设备或已审核调焦限额。请核对并重新生成本夜配置；界面配对不能代替设备标定或运动许可。", "Night Setup lacks matching photometry equipment or reviewed focus limits. Check and regenerate the setup; UI pairing cannot replace calibration or movement permission.", culture),
            "PHOTOMETRY_FILTER_AMBIGUOUS" or "PHOTOMETRY_FILTER_ORIGIN_UNKNOWN" or "PHOTOMETRY_FILTER_POSITION_UNCONFIRMED" => T("滤镜名称或实际轮位未能唯一确认。请核对测光端滤镜表和轮位读回，再由主控重新检查任务。", "The filter name or physical slot is not confirmed unambiguously. Check the photometry filter table and position readback before the master rechecks the job.", culture),
            "PHOTOMETRY_COOLING_UNCONFIRMED" => T("测光相机温控尚未确认达到本轮要求。请检查制冷开关、设定温度与实际温度；保存地址或允许接收不代表温控就绪。", "Photometry camera cooling is not confirmed against the run requirements. Check cooler, setpoint and actual temperature; pairing or reception does not imply thermal readiness.", culture),
            "PHOTOMETRY_FRAME_MODE_UNSUPPORTED" => T("本版只支持 16 位全帧测光采集。请核对位深、硬件裁切及保存格式，不要将不支持的模式当作有效数据。", "This version requires 16-bit full-frame photometry capture. Check bit depth, hardware ROI and saving format; an unsupported mode is not valid data.", culture),
            "PHOTOMETRY_FITS_PROVENANCE_MISMATCH" or "PHOTOMETRY_FRAME_REUSED" => T("保存图像的身份或任务关联不符合本轮要求。原始文件已保留，请核对技术详情；不能把旧图或其他任务的图像计入本轮。", "Saved-frame identity or job association does not match this run. Raw files are retained; inspect technical details. Old frames or another job's images cannot count toward this run.", culture),
            "PHOTOMETRY_PEER_BINDING_MISMATCH" or "PHOTOMETRY_MASTER_SESSION_CHANGED" or "PHOTOMETRY_WORKER_SESSION_CHANGED" => T("两端的配置编号或运行会话已经变化。请先结束旧任务，再核对配对信息并重新允许接收；重启后不会自动认领旧任务。", "Peer Profile IDs or sessions changed. End the old job, check pairing and enable reception again. Restarting never silently adopts old jobs.", culture),
            "PHOTOMETRY_ROLE_REQUIRED" => T("请先把本窗口选为“测光端”。光谱主控不在这里绑定测光设备。", "Select the Photometry instance role first. Do not bind photometry devices in the spectroscopy master.", culture),
            "PHOTOMETRY_SHARED_DEVICE_FORBIDDEN" => T("测光端配置里仍选有公共设备。请到 N.I.N.A. 设备页取消赤道仪、导星、屋顶等选择；仅断开连接不够。具体类别见技术详情。", "Shared equipment is still selected in the photometry Profile. Deselect telescope, guider, roof and other shared devices; disconnecting alone is insufficient. See technical details for the categories.", culture),
            "PHOTOMETRY_SHARED_TRIGGER_FORBIDDEN" => T("请在测光端的滤镜轮设置中关闭“换滤镜时暂停导星”；导星只能由光谱主控调度。", "Disable 'stop guiding on filter change' in the photometry filter-wheel settings. Only the spectroscopy master may manage guiding.", culture),
            "PHOTOMETRY_NATIVE_FITS_REQUIRED" => T("请先在测光端 N.I.N.A. 设置输出目录，并选择未压缩 FITS 格式，然后重新开始接收。", "Set the photometry instance's output directory and select uncompressed FITS, then enable reception again.", culture),
            "PHOTOMETRY_DEVICE_BINDING_CHANGED" => T("当前相机、滤镜轮或电调焦与绑定记录不一致。请先选对测光设备，再点击“记录所选设备”。", "The selected camera, wheel or focuser differs from the binding. Select the correct photometry devices, then record the selection.", culture),
            "PHOTOMETRY_LEGACY_OWNER_RUNNING" => T("旧版采集服务仍在运行。请在无曝光时停止并确认其释放测光设备，再使用测光 N.I.N.A.；程序不会自动抢占。", "The legacy acquisition service is still running. While idle, stop it and confirm it has released the photometry devices. This instance will not seize them.", culture),
            "PHOTOMETRY_CONFIGURATION_CHANGED" => T("启用后配置发生变化。请停止任务，待结束后重新启动本测光实例并核对配对；旧任务不会自动接续。", "Configuration changed after reception was enabled. Stop the job, then restart this photometry instance while idle and check pairing; old jobs are not resumed automatically.", culture),
            "PHOTOMETRY_ENABLE_WHILE_IDLE" => T("请等 N.I.N.A. 初始化完成，并在测光高级序列启动前点击“开始接收主控任务”。", "Wait for N.I.N.A. to initialize; enable reception before starting a photometry receiver sequence.", culture),
            "PHOTOMETRY_RECEIVER_NOT_ACTIVE" or "PHOTOMETRY_RECEIVER_NOT_ARMED" => T("接收项尚未就绪。请先允许本次会话接收，再启动包含测光协同接收项的高级序列。", "The receiver is not ready. Enable reception for this session, then start the reviewed receiver sequence.", culture),
            "PHOTOMETRY_OPERATOR_PAUSED" => T("人工暂停仍有效，不会自动覆盖。停止旧任务并确认结束后，才可允许接收新任务。", "Operator pause is still active. Stop the old job and confirm completion before allowing new jobs.", culture),
            "PHOTOMETRY_STOP_UNCONFIRMED" => T("还没有确认曝光或保存已经结束。请检查测光端原生相机状态，不要把正在停止当作已停止。", "Exposure or saving has not reached a confirmed terminal state. Check the native camera; stopping is not stopped.", culture),
            _ => null,
        };
        if (known is not null) return known;
        if (raw.StartsWith("Bind a different spectroscopy", StringComparison.Ordinal)) return T("主控配置编号无效或与本端相同。请从另一个光谱主控窗口复制完整编号。", "The master Profile ID is invalid or matches this instance. Copy the full ID from the other spectroscopy master window.", culture);
        if (raw.StartsWith("Select the photometry filter wheel", StringComparison.Ordinal)) return T("请先在本测光实例选择测光滤镜轮和测光电调焦，再记录设备。", "Select the photometry filter wheel and focuser in this instance before recording devices.", culture);
        if (raw.StartsWith("Select the exact native QHY", StringComparison.Ordinal) || raw.StartsWith("Select N.I.N.A.'s native QHY", StringComparison.Ordinal)) return T("当前版本的测光适配器支持原生 QHYminiCam8M。请核对所选驱动；改用通用界面名称不代表其他型号已验证支持。", "This version's photometry adapter supports native QHYminiCam8M. Check the selected driver; generic UI labels do not imply support for unvalidated models.", culture);
        if (raw.StartsWith("Restart the idle worker", StringComparison.Ordinal)) return T("本实例保留了旧会话。请在任务结束后重启测光 N.I.N.A.，再开始接收；不会删除旧记录。", "This instance retains an earlier session. Restart the photometry N.I.N.A. while idle, then enable reception; old records are retained.", culture);
        if (raw.StartsWith("End the previous job", StringComparison.Ordinal)) return T("上一任务还未结束。请先停止并等待确认，再允许接收新任务。", "The previous job is not terminal. Stop it and wait for confirmation before allowing new jobs.", culture);
        return T("本次操作未完成。请展开技术详情查看具体原因；没有跳过设备或观测检查。", "The operation did not complete. Expand technical details for the cause; equipment and observing checks were not bypassed.", culture);
    }

    private static string T(string zh, string en, CultureInfo? culture) => ObservationUiPresentation.Text(zh, en, culture);
}
