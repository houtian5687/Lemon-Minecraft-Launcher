using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace App1.Core
{
    /// <summary>
    /// UI 线程调度助手，供后台线程安全更新界面。
    /// </summary>
    public static class UiDispatcher
    {
        public static DispatcherQueue? Queue { get; set; }

        public static bool HasThreadAccess => Queue?.HasThreadAccess ?? false;

        public static void Run(Action action)
        {
            if (Queue is null || Queue.HasThreadAccess)
            {
                action();
                return;
            }
            Queue.TryEnqueue(() =>
            {
                try { action(); }
                catch (Exception ex) { Logger.Error("UI 调度执行异常", ex); }
            });
        }

        public static Task RunAsync(Action action)
        {
            var tcs = new TaskCompletionSource();
            Run(() =>
            {
                try { action(); tcs.TrySetResult(); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }
    }
}
