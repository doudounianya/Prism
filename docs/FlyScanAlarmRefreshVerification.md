# FlyScan 实时报警刷新离线验证

验证日期为 2026-11-26。使用 Windows、AMD Ryzen 7 255 和 .NET 8.0.34，两个版本均以 Release 构建。优化前版本为 GitHub main 上的 31f118c，优化后版本在同一基线上单独应用本次改动。

## 测量方法

- 两个版本共用一份含 50,000 条观测的合成 SQLite 历史。待测程序各自在临时副本上加入相同的报警和心跳，为实时页面提供在线且有活动报警的场景。每次执行前验证了输入文件的 SHA-256 一致。
- 使用共用的 WPF 实时报警页面和 FlyScan 实际视图模型。预热 20 次后，在各自进程内连续执行 300 次刷新，计入视图模型查询、WPF 数据绑定和离屏布局。
- 每个版本执行三轮，第二轮交换执行顺序。刷新耗时、UI 线程分配字节数和各代 GC 次数均在这 300 次内采集。数据库打开、启动时恢复活动报警、预热和后续统计比对均在计时区间外。

输入 SHA-256 为 `3E609EF3C67F64BC3C87B4FA8DB5CEFEF176E2BB20A9F70733A9E551C0606A46`。

## 刷新性能

| 版本 | 轮次 | 300 次总耗时（ms） | 单次中位耗时（ms） | P95（ms） | UI 线程分配（字节） | Gen 0 / 1 / 2 次数 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 优化前 | 1 | 13,630.15 | 39.05 | 80.64 | 3,666,050,160 | 444 / 161 / 6 |
| 优化前 | 2 | 12,555.50 | 39.80 | 56.58 | 3,666,059,464 | 445 / 168 / 7 |
| 优化前 | 3 | 13,491.83 | 43.08 | 58.18 | 3,666,057,680 | 445 / 169 / 7 |
| 优化后 | 1 | 1,789.85 | 5.50 | 9.55 | 89,147,744 | 11 / 5 / 1 |
| 优化后 | 2 | 1,737.42 | 5.70 | 7.60 | 89,149,344 | 11 / 5 / 1 |
| 优化后 | 3 | 1,773.31 | 5.61 | 9.10 | 89,145,560 | 11 / 5 / 1 |

按各指标的三轮中位值计算，单次中位耗时下降 85.9%，P95 下降 84.4%，UI 线程分配量下降 97.6%。每次刷新平均分配从 11.65 MiB 降至 0.28 MiB。

## 报警时长与断线缺口

- 两个版本的完整历史均包含 2,002 条报警和 1,102 个采集缺口。报警字段、已确认片段和缺口边界序列化后的 SHA-256 均为 `F92EC13A2D28B8B74D146080AB0275A5FC6FB59655F4A9690B3E6D8F6365E7C2`。累计已确认报警时长均为 58,006 秒，包含相互重叠的报警时段。
- 在线、断线、重连、反馈超时、重启和重启后解除六个阶段的查询结果逐项一致。在线到反馈超时四个阶段的 WPF 行数据也一致。示例断线区间为 08:00:02 至 08:00:08（UTC），重连时运动卡告警仍活动，已确认时长为 3 秒。
- FlyScan 流程回归通过 93 项，包含启动恢复、同一时间戳的观测、心跳扩展、断线和暂时超时。FlyScan WPF 回归通过 70 项。PLC HMI Debug 解决方案构建通过。
- 实时页从内存读取活动报警，同时开始的报警按告警码排序。应用打开数据库时从最近一条在线且全部报警已解除的观测之后恢复活动报警。缺少该观测的数据库在打开时需要回放历史。历史报警和报警分析继续从持久化观测计算。

## 复跑

在优化前和优化后的源码副本中使用同一份 `AlarmRefreshBenchmark.cs` 和 `SharedControlPages.Smoke` 命令行入口。分别构建 Release 版 `SharedControlPages.Smoke`。

```powershell
dotnet build tests/SharedControlPages.Smoke/SharedControlPages.Smoke.csproj -c Release -p:CompanyProduct=FlyScan
```

对下面的两个变量填写对应版本的 `SharedControlPages.Smoke.exe` 完整路径。使用空目录存放新建的 SQLite 和 JSON 文件，每轮使用新的输出路径。

```powershell
& $baselineProbe --alarm-refresh-seed "$PWD/fixture.sqlite"
& $baselineProbe --alarm-refresh-benchmark "$PWD/fixture.sqlite" "$PWD/baseline-1.json"
& $candidateProbe --alarm-refresh-benchmark "$PWD/fixture.sqlite" "$PWD/candidate-1.json"
```

对比 JSON 中的 `FixtureSha256`、`FullHistory` 和 `Snapshots`，三者应完全相等。
