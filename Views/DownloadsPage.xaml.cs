using App1.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace App1.Views
{
    public sealed partial class DownloadsPage : Page
    {
        public DownloadsViewModel ViewModel { get; } = new();

        public DownloadsPage()
        {
            InitializeComponent();
            DataContext = ViewModel;
            Loaded += (_, _) => ViewModel.Start();
            Unloaded += (_, _) => ViewModel.Stop();
        }
    }
}
