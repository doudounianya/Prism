using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Company.Core.Extension
{
    public static class CancellationTokenObservableExtensions
    {
        /// <summary>
        /// 将 CancellationToken 转换为 IObservable<Unit>（取消时发送信号）
        /// </summary>
        public static IObservable<Unit> ToObservable(this CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return Observable.Return(Unit.Default);
            }

            return Observable.Create<Unit>(observer =>
            {
                return token.Register(() =>
                {
                    observer.OnNext(Unit.Default);
                    observer.OnCompleted();
                });
            });
        }
    }
}
