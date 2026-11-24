namespace Company.Hardware.ControlCard;

/// <summary>
/// 一台运动控制器的统一入口。工厂负责选择实现，初始化负责建立会话，状态读取负责确认执行结果。
/// 接口包含公共运动、数字 IO、身份能力及轨迹任务；厂商未实现的功能必须明确拒绝，不能空操作返回成功。
/// bool 表示本次请求或读取是否成功，不表示机械动作已经完成；完成须另核对有效反馈。
/// </summary>
public interface IMotionController
{
    #region 会话与配置

    /// <summary>当前会话已经完成打开、配置及首次状态读取。</summary>
    bool IsInitialized { get; }
    /// <summary>控制器最近确认的就绪状态；每次具体运动仍会重新校验对应轴。</summary>
    bool IsReady { get; }
    /// <summary>
    /// 显式初始化，并深复制本次配置。调用方随后修改原配置不影响已建立的会话。
    /// 默认不启用硬件；真实驱动还须允许原生访问并满足厂商协议确认选项。
    /// </summary>
    bool Initialize(MotionConfiguration configuration);
    /// <summary>尽力停止、关闭设备并使会话失效；资源释放失败时保留重试责任。</summary>
    void Close();

    #endregion

    #region 反馈快照

    /// <summary>每次返回独立副本；先核对 Valid，再解释位置、使能、报警及运动状态。</summary>
    IReadOnlyList<AxisState> AxisStates { get; }
    IReadOnlyList<CoordinateState> CoordinateStates { get; }
    IReadOnlyList<DigitalPointState> InputStates { get; }
    IReadOnlyList<DigitalPointState> OutputStates { get; }
    /// <summary>读取指定轴；null 表示当前配置的全部逻辑轴。</summary>
    bool RefreshAxisState(AxisId? axisId);
    /// <summary>读取指定坐标系；null 表示当前配置的全部逻辑坐标系。</summary>
    bool RefreshCoordinateState(CoordinateId? coordinateId);

    #endregion

    #region 单轴运动

    /// <summary>请求指定轴或全部轴使能／关闭使能；不以修改反馈对象代替硬件命令。</summary>
    bool SetAxisEnabled(AxisId? axisId, bool enabled, out string message);
    /// <summary>仅执行厂商已明确实现的故障复位，不自动清除软件状态来冒充复位。</summary>
    bool ResetAxisFault(AxisId? axisId, out string message);
    bool HomeAxis(AxisId axisId, out string message);
    /// <summary>按已确认轴单位移动到绝对位置。</summary>
    bool MoveAbsolute(AxisId axisId, double position, out string message);
    /// <summary>相对本次有效反馈位置移动指定距离。</summary>
    bool MoveRelative(AxisId axisId, double distance, out string message);
    bool JogAxis(AxisId axisId, JogDirection direction, out string message);
    bool PauseAxis(AxisId axisId, out string message);
    bool ResumeAxis(AxisId axisId, out string message);
    /// <summary>显式请求停止并保留结果；请求成功后仍须读取状态确认停稳。</summary>
    bool TryStop(AxisId? axisId, StopMode mode, out string message);
    /// <summary>
    /// 有界、可取消地等待轴空闲。取消等待不自动停止轴；空闲也不自动等同于定位或回零成功。
    /// 等待期间逐次释放调用锁，允许其它线程请求停止或关闭。
    /// </summary>
    bool WaitForAxisIdle(AxisId axisId, TimeSpan timeout, CancellationToken cancellationToken, out string message);

    #endregion

    #region 坐标系与轨迹

    /// <summary>提交一组 XYZ 绝对位置点，使用与轨迹任务相同的校验和轴占用逻辑。</summary>
    bool MoveLinearPath(CoordinateId coordinateId, List<Position3D> points, out string message);
    /// <summary>提交不可变的直线／平面圆弧段；是否支持由具体驱动报告。</summary>
    bool StartTrajectory(CoordinateId coordinateId, IReadOnlyList<TrajectorySegment> segments, out string message);
    bool PauseCoordinate(CoordinateId coordinateId, out string message);
    bool ResumeCoordinate(CoordinateId coordinateId, out string message);
    /// <summary>清理本会话可操作的坐标系；未知占用或未停稳不能直接释放。</summary>
    bool ClearCoordinate(CoordinateId coordinateId, out string message);
    /// <summary>请求停止并清理本任务；取消不表示到达原目标。</summary>
    bool CancelTrajectory(CoordinateId coordinateId, out string message);
    /// <summary>等待本次任务版本完成；取消只退出等待，停止任务需单独调用 CancelTrajectory。</summary>
    bool WaitForTrajectory(CoordinateId coordinateId, TimeSpan timeout, CancellationToken cancellationToken, out string message);

    #endregion

    #region 数字输入输出

    bool RefreshInputs(out string message);
    bool RefreshOutputs(out string message);
    /// <summary>按 Bank/Bit 地址写输出；每组 16 位，必须属于当前控制器配置。</summary>
    bool WriteOutput(DigitalPointState point, bool value, out string message);

    #endregion

    #region 身份与能力

    /// <summary>读取已保存的能力快照，分别表示硬件支持、实现程度、验证程度和当前可执行条件。</summary>
    IReadOnlyList<MotionCapability> GetCapabilities();
    /// <summary>读取已保存的设备身份，未知字段保留 Unknown；查询本身不连接硬件。</summary>
    MotionControllerIdentity GetIdentity();

    #endregion
}
