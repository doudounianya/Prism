using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Core.Extension
{
    /// <summary>
    /// 对话框扩展类
    /// </summary>
    public static class DialogServiceExtension
    {
        /// <summary>
        /// 以阻塞方式弹出非模态对话框
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="dialogService"></param>
        /// <param name="callback"></param>
        /// <param name="param"></param>
        public static void ShowDialog<T>(this IDialogService dialogService, Action<IDialogResult> callback, DialogParameters param = null)
        {
            dialogService.ShowDialog(typeof(T).Name, param ?? new DialogParameters(), callback);
        }
    }
}
