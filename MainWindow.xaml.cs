using System.Windows;
using TwinSync_Gateway.ViewModels;

namespace TwinSync_Gateway
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }
    }
}
