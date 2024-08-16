using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Cyaim.WebSocketServer.Example.Wpf
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 打印日志到Text
        /// </summary>
        /// <param name="message"></param>
        public async void AppendLog(string message)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                LogText.AppendText(message + Environment.NewLine);
                LogText.ScrollToEnd();
            });
        }
    }
}