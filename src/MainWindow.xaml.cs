using diskusage.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace diskusage
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            using (var p = Process.GetCurrentProcess())
                p.PriorityClass = ProcessPriorityClass.Idle;

            DriveInfo.GetDrives()
                .Select(s => new FsNode(s.RootDirectory))
                .ToList()
                .ForEach(s => fileView.Items.Add(s));
        }

        private void TreeViewItem_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TreeViewItem tvi)
            {
                e.Handled = true;
                tvi.IsSelected = true;
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            var fullName = ((sender as MenuItem)?.DataContext as FsNode)?.FileSystemInfo.FullName;
            if(!string.IsNullOrWhiteSpace(fullName))
            {
                Clipboard.SetText(fullName);
            }
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var fsi = ((sender as MenuItem)?.DataContext as FsNode)?.FileSystemInfo;
            if(fsi is DirectoryInfo di)
            {
                Process.Start("explorer.exe", di.FullName);
            } 
            else if(fsi is FileInfo fi)
            {
                Process.Start("explorer.exe", $"/select,\"{fi.FullName}\"");
            }
        }
    }
}
