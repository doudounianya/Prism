using Company.FlyScan.Runtime;
using Company.Hmi.Presentation;
using System.Windows.Controls;

namespace Company.FlyScan.Shell;

public partial class MainWindow
{
    private void SetupHistoryPages(FlyScanHistory history)
    {
        var live = new FlyAlarmPage(history, live: true);
        var alarmHistory = new FlyAlarmPage(history);
        var analysis = new FlyAlarmPage(history, openHistory: selection =>
        {
            alarmHistory.Filter.Apply(selection);
            AlarmPages.SelectedItem = AlarmHistoryTab;
        });
        var today = new FlyProductionPage(history, ProductionPageKind.Today);
        var daily = new FlyProductionPage(history, ProductionPageKind.Daily);
        var trend = new FlyProductionPage(history, ProductionPageKind.Trend, openDaily: date =>
        {
            daily.Filter.Date = date;
            ProductionPages.SelectedItem = DailyTab;
        });
        Bind(RealtimePage, live);
        Bind(AlarmHistoryPage, alarmHistory);
        Bind(AlarmAnalysisPage, analysis);
        Bind(TodayPage, today);
        Bind(DailyPage, daily);
        Bind(TrendPage, trend);
        Bind(LogPage, new FlyOperationPage(history));

        RealtimePage.PageRequested += NavigateAlarm;
        AlarmHistoryPage.PageRequested += NavigateAlarm;
        AlarmAnalysisPage.PageRequested += NavigateAlarm;
        TodayPage.PageRequested += NavigateProduction;
        DailyPage.PageRequested += NavigateProduction;
        TrendPage.PageRequested += NavigateProduction;

        _summaryRefresh.Tick += (_, _) => { live.Query(); today.Query(); UpdateDevice(); };
        _summaryRefresh.Start();
        DevicePage.PageVisibilityChanged += visible => { if (visible) UpdateDevice(); };
        UpdateDevice();

        void UpdateDevice()
        {
            if (!DevicePage.IsVisible) return;
            var row = new DeviceRow("IMC30G-E 仿真", history.Online ? "通讯正常" : "反馈未知",
                history.LastConfirmed is { } time ? FlyAlarmPage.Stamp(time) : "—",
                history.Online ? "本机仿真反馈" : "等待飞拍仿真有效反馈");
            if (DevicePage.DataContext is not DeviceDisplay current || current.Devices[0] != row)
                DevicePage.DataContext = new DeviceDisplay([row]);
        }
    }

    private static void Bind(BusinessPage view, HistoryPage model)
    {
        view.DataContext = model;
        view.PageVisibilityChanged += model.SetVisible;
    }

    private void NavigateAlarm(string page) => AlarmPages.SelectedItem = page switch
    {
        "AlarmHistoryView" => AlarmHistoryTab,
        "AlarmAnalysisView" => AlarmAnalysisTab,
        _ => RealtimeTab
    };

    private void NavigateProduction(string page) => ProductionPages.SelectedItem = page switch
    {
        "ProductionDailyView" => DailyTab,
        "ProductionTrendView" => TrendTab,
        _ => TodayTab
    };

    private sealed record DeviceDisplay(DeviceRow[] Devices);
    private sealed record DeviceRow(string Device, string State, string LastSuccess, string Error);
}
