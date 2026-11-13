/*
 * 文件职责与维护说明：
 * 气缸单个方向的显示组合，缩回和伸出各有一行。
 * 命令、输入、完成、报警、耗时和延时都引用现有字段；可选字段为空表示没有该项来源，不凭空生成信号。
 */
namespace Company.Application.Main.Models;

public sealed record CylinderDirectionRow(string Direction, PlcDisplayField? CommandFeedback, PlcDisplayField? Sensor,
    PlcDisplayField? Complete, PlcDisplayField? Alarm, PlcDisplayField? Elapsed,
    PlcDisplayField? ArrivalDelay = null, PlcDisplayField? AlarmDelay = null);
