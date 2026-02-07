using System.Configuration;
using System.Data;
using System.Windows;
using TwinSync_Gateway.ViewModels;

namespace TwinSync_Gateway
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Create MainWindow normally (it creates MainViewModel via XAML)
            var win = new MainWindow();
            win.Show();

            // Get the VM from DataContext
            if (win.DataContext is MainViewModel vm)
            {
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
                await vm.StartIotAsync();
            }
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            base.OnExit(e);
            // Get the MainWindow and its VM
            if (Current.MainWindow is MainWindow win && win.DataContext is MainViewModel vm)
            {
                await vm.StopIotAsync();
            }
        }
    }
}
