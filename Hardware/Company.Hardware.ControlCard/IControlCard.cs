using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Company.Hardware.ControlCard
{
    /// <summary>
    /// 运动控制卡的接口
    /// </summary>
    public interface IControlCard
    {
        /// <summary>
        /// 表示控制卡是否初始化完成
        /// </summary>
        bool Initialized { get; }

        /// <summary>
        /// 表示控制卡是否准备好
        /// </summary>
        bool IsReady { get; }

        /// <summary>只读显示快照；每次读取均克隆，调用方不得以修改显示值代替硬件操作。</summary>
        IReadOnlyList<AxisStatus> AxisStatuss { get; }
        IReadOnlyList<CrdStatus> CrdStatuss { get; }
        IReadOnlyList<BitStatus> DiStatuss { get; }
        IReadOnlyList<BitStatus> DoStatuss { get; }

        /// <summary>设置指定轴或全部轴的硬件使能；只返回指令是否接受，不替代状态回读。</summary>
        bool SetAxisEnable(AxisType? axisType, bool value, out string message);

        /// <summary>请求厂商明确支持的报警复位；不支持时受控返回 false。</summary>
        bool ResetAxisFault(AxisType? axisType, out string message);

        /// <summary>以明确模式请求停止；返回 false 表示停止未确认，不能当作已经停稳。</summary>
        bool TryStop(AxisType? axisType, AxisStopMode mode, out string message);

        /// <summary>
        /// 有界等待 SDK 当前读取为空闲。不会在轮询等待期间占用控制卡调用锁。
        /// 轴空闲不等同定位或回零成功，调用方仍应核对到位/回零状态；取消等待不自动停止轴。
        /// </summary>
        bool WaitForAxisIdle(AxisType axisType, TimeSpan timeout, CancellationToken cancellationToken, out string message);

        /// <summary>
        /// 初始化控制卡
        /// </summary>
        /// <returns></returns>
        bool Init(IControlCardConfigProvider provider);

        /// <summary>
        /// 关闭控制卡
        /// </summary>
        void Close();

        /// <summary>
        /// 运动轴状态
        /// </summary>
        /// <param name="axisType">轴的类型</param>
        /// <param name="message">信息</param>
        /// <returns></returns>
        public bool GetAxisStatus(AxisType? axisType);

        /// <summary>
        /// 运动轴回零
        /// </summary>
        /// <param name="axisType">轴的类型</param>
        /// <param name="message">信息</param>
        /// <returns></returns>
        bool GoHome(AxisType axisType, out string message);

        /// <summary>
        /// 运动轴绝对移动
        /// </summary>
        /// <param name="axisType">轴的类型</param>
        /// <param name="message">信息</param>
        /// <returns></returns>
        bool PtpSingleMoveTo(AxisType axisType, Double pos, out string message);

        /// <summary>
        /// 运动轴相对移动
        /// </summary>
        /// <param name="axisType">轴的类型</param>
        /// <param name="message">信息</param>
        /// <returns></returns>
        bool PtpSingleMoveBy(AxisType axisType, Double pos, out string message);

        /// <summary>
        /// 运动轴PTP暂停
        /// </summary>
        /// <param name="axisType">轴的类型</param>
        /// <param name="message">信息</param>
        /// <returns></returns>
        bool PtpSinglePause(AxisType axisType, out string message);

        /// <summary>
        /// 运动轴PTP暂停恢复
        /// </summary>
        /// <param name="axisType">轴的类型</param>
        /// <param name="message">信息</param>
        /// <returns></returns>
        bool PtpSingleResume(AxisType axisType, out string message);

        /// <summary>
        /// 运动轴JOG运动
        /// </summary>
        /// <param name="axisType">轴的类型</param>
        /// <param name="moveDirection">方向</param>
        /// <returns></returns>
        bool JogSingleMove(AxisType axisType, MoveDirection moveDirection, out string message);

        /// <summary>
        /// 运动轴停止轴运动
        /// </summary>
        /// <param name="axisType">轴的类型</param>
        void Stop(AxisType? axisType);





        /// <summary>
        /// 运动轴状态
        /// </summary>
        /// <param name="axisType">坐标系的类型</param>
        /// <param name="message">信息</param>
        /// <returns></returns>
        public bool GetCrdStatus(CrdType? crdType);
        /// <summary>
        /// XYZ坐标系直线插补运动
        /// </summary>
        /// <param name="axisType">坐标系的类型</param>
        bool CrdLineMoveTo_XYZ(CrdType crdType, List<PosXYZ> pos, out string message);


        /// <summary>
        /// 坐标系插补暂停
        /// </summary>
        /// <param name="axisType">坐标系的类型</param>
        /// <param name="message">信息</param>
        /// <returns></returns>
        bool CrdPause(CrdType crdType, out string message);

        /// <summary>
        /// 坐标系插补暂停恢复
        /// </summary>
        /// <param name="axisType">坐标系的类型</param>
        /// <param name="message">信息</param>
        /// <returns></returns>
        bool CrdResume(CrdType crdType, out string message);

        /// <summary>
        /// 坐标系插补清空
        /// </summary>
        /// <param name="axisType">坐标系的类型</param>
        /// <param name="message">信息</param>
        /// <returns></returns>
        bool CrdClr(CrdType crdType, out string message);





        /// <summary>
        /// Input状态
        /// </summary>
        /// <param name="message">信息</param>
        /// <returns></returns>
        bool InputGetStatus(out string message);

        /// <summary>
        /// Output状态
        /// </summary>
        /// <param name="message">信息</param>
        /// <returns></returns>
        bool OutputGetStatus(out string message);

        /// <summary>
        /// Output设置
        /// </summary>
        /// <param name="message">信息</param>
        /// <returns></returns>
        bool OutputSetStatus(BitStatus bitStatus, bool value, out string message);



    }
}
