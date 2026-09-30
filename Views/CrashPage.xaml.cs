using App1.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace App1.Views
{
    public sealed partial class CrashPage : Page
    {
        public CrashViewModel ViewModel { get; } = new();

        public CrashPage()
        {
            InitializeComponent();
            DataContext = ViewModel;
            Loaded += async (_, _) => await ViewModel.InitializeAsync();
        }
    }
}
