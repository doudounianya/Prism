/*
 * 文件职责与维护说明：
 * WPF 宿主程序集的资源主题声明。
 * ThemeInfo 决定控件主题字典的查找范围，是程序集元数据；本文件不参与页面导航或 PLC 通讯。
 */
using System.Windows;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
