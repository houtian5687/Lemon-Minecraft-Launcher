using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using App1.Core;
using App1.Models;
using App1.Services;
using Microsoft.UI.Xaml;

namespace App1.ViewModels
{
    public sealed class LogFileItem(string path)
    {
        public string Path { get; } = path;
        public string Name { get; } = System.IO.Path.GetFileName(path);
        public string Time { get; } = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss");
        public string SizeText { get; } = new FileInfo(path).Length.FormatSize();
        public override string ToString() => $"{Name}（{Time}）";
    }

    /// <summary>
    /// 崩溃分析页：列出历史日志并给出问题定位与修复建议。
    /// </summary>
    public sealed class CrashViewModel : ObservableObject
    {
        private LogFileItem? _selectedLog;
        private CrashReport? _report;
        private bool _isBusy;

        public ObservableCollection<LogFileItem> Logs { get; } = new();

        public LogFileItem? SelectedLog
        {
            get => _selectedLog;
            set
            {
                if (Set(ref _selectedLog, value))
                {
                    Raise(nameof(HasSelection));
                    AnalyzeCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool HasSelection => SelectedLog is not null;

        public CrashReport? Report
        {
            get => _report;
            set
            {
                if (Set(ref _report, value))
                    Raise(nameof(HasReport), nameof(HasIssues), nameof(HasNoIssues));
            }
        }

        /// <summary>是否已产生分析结果。</summary>
        public bool HasReport => _report is not null;

        /// <summary>分析结果中是否包含问题条目。</summary>
        public bool HasIssues => _report is { Issues.Count: > 0 };

        /// <summary>分析完成但未匹配到已知问题。</summary>
        public bool HasNoIssues => _report is { Issues.Count: 0 };

        public bool IsBusy
        {
            get => _isBusy;
            set => Set(ref _isBusy, value);
        }

        public RelayCommand RefreshCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        public RelayCommand CopyReportCommand { get; }
        public RelayCommand OpenLogFolderCommand { get; }
        public RelayCommand AnalyzeLatestCommand { get; }

        public CrashViewModel()
        {
            RefreshCommand = new RelayCommand(Refresh);
            AnalyzeCommand = new RelayCommand(Analyze, () => HasSelection);
            AnalyzeLatestCommand = new RelayCommand(() =>
            {
                if (Logs.Count > 0)
                {
                    SelectedLog = Logs[0];
                    Analyze();
                }
            }, () => Logs.Count > 0);
            CopyReportCommand = new RelayCommand(CopyReport, () => Report is not null);
            OpenLogFolderCommand = new RelayCommand(() =>
            {
                Directory.CreateDirectory(AppPaths.GameLogsRoot);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.GameLogsRoot)
                {
                    UseShellExecute = true
                });
            });
        }

        public Task InitializeAsync()
        {
            Refresh();
            if (Logs.Count > 0) SelectedLog = Logs[0];
            return Task.CompletedTask;
        }

        public void Refresh()
        {
            Logs.Clear();
            foreach (var file in CrashAnalysisService.ListGameLogs())
            {
                Logs.Add(new LogFileItem(file.FullName));
            }
        }

        public void Analyze()
        {
            if (SelectedLog is null) return;
            IsBusy = true;
            try
            {
                Report = AppServices.CrashAnalyzer.AnalyzeFile(SelectedLog.Path);
                CopyReportCommand.NotifyCanExecuteChanged();
                AppServices.NotifyInfo("分析完成：" + Report.Summary);
            }
            catch (Exception ex)
            {
                AppServices.NotifyError("分析失败", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void CopyReport()
        {
            if (Report is null) return;
            try
            {
                var text = CrashAnalysisService.ToShareText(Report);
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                AppServices.NotifyInfo("崩溃报告已复制到剪贴板");
            }
            catch (Exception ex)
            {
                AppServices.NotifyError("复制失败", ex.Message);
            }
        }
    }
}
