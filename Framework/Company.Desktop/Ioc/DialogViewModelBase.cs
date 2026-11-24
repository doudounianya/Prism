using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Core.Ioc
{
    public class DialogViewModelBase : BindableBase, IDialogAware
    {

        public string Title { get; set; } = "对话框标题";

        //public event Action<IDialogResult> RequestClose;

        /// <summary>
        /// 关闭对话框前，判断是否可以关闭
        /// </summary>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public virtual bool CanCloseDialog()
        {
            return true;
        }

        /// <summary>
        /// 关闭时执行的方法
        /// </summary>
        public virtual void OnDialogClosed()
        {

        }

        /// <summary>
        /// 打开时执行的方法
        /// </summary>
        /// <param name="parameters"></param>
        /// <exception cref="NotImplementedException"></exception>
        public virtual void OnDialogOpened(IDialogParameters parameters)
        {

        }

        /// <summary>
        /// 关闭对话框
        /// </summary>
        public DialogCloseListener RequestClose { get; private set; }



        /// <summary>
        /// 【新增】提供一个受保护的或公共的方法，用于子类触发关闭对话框
        /// </summary>
        /// <param name="buttonResult">关闭结果（OK, Cancel, Yes, No 等）</param>
        protected virtual void CloseDialog(ButtonResult buttonResult)
        {
            // 构造 DialogResult
            var dialogResult = new DialogResult(buttonResult);
            // 触发关闭事件
            RequestClose.Invoke(dialogResult);
        }



        /// <summary>
        /// 关闭对话框（可选带参数）
        /// </summary>
        protected virtual void CloseDialog(ButtonResult buttonResult, IDialogParameters? parameters = null)
        {
            var dialogResult = new DialogResult(buttonResult)
            {
                Parameters = parameters ?? new DialogParameters()
            };
            RequestClose.Invoke(dialogResult);
        }

    }
}
