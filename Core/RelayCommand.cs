using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace App1.Core
{
    /// <summary>
    /// 同步命令实现。
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute is null ? null : _ => canExecute())
        {
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 异步命令实现，执行期间自动禁用，避免重复触发。
    /// </summary>
    public sealed class AsyncRelayCommand : ICommand, IDisposable
    {
        private readonly Func<object?, Task> _execute;
        private readonly Func<object?, bool>? _canExecute;
        private bool _isRunning;

        public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute is null ? null : _ => canExecute())
        {
        }

        public event EventHandler? CanExecuteChanged;

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (_isRunning == value) return;
                _isRunning = value;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

        /// <summary>
        /// 以异步方式执行命令（供代码调用并等待结果）。
        /// </summary>
        public Task ExecuteAsync(object? parameter) => _execute(parameter);

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            IsRunning = true;
            try
            {
                await _execute(parameter);
            }
            catch (Exception ex)
            {
                App1.Core.Logger.Error("命令执行失败", ex);
                App1.Services.AppServices.NotifyError("操作失败：" + ex.Message);
            }
            finally
            {
                IsRunning = false;
            }
        }

        public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

        public void Dispose() { }
    }
}
