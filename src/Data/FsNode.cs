using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Humanizer;

namespace diskusage.Data
{

    public class FsNode : INotifyPropertyChanged
    {
        static readonly ImageSource FolderIcon;
        static FsNode()
        {
            using (var imgStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("diskusage.folder.png"))
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.StreamSource = imgStream;
                img.EndInit();
                img.Freeze();
                FolderIcon = img;
            }
        }

        public FileSystemInfo FileSystemInfo { get; }
        public ImageSource ImageSource { get; private set; } = null;
        public DirectoryDetails DirectoryDetails { get; private set; } = null;
        public bool IsRoot => FileSystemInfo is DirectoryInfo di && di.Parent == null;
        public bool IsHidden => (FileSystemInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden;
        public double Opacity => IsHidden && !IsRoot ? .75d : 1d;
        private long? _nodeSize = null;
        public long? NodeSize
        {
            get => _nodeSize;
            set
            {
                _nodeSize = value;
                OnPropertyChanged(nameof(NodeSize));
                OnPropertyChanged(nameof(FullDetails));
            }
        }

        //public long? NodeSize { get; private set; } = null;
        public string DisplaySize => NodeSize?.Bytes().ToString("#.#");


        private long? _parentSize = null;
        public long? ParentSize
        {
            get => _parentSize;
            set { 
                _parentSize = value;
                OnPropertyChanged(nameof(Percentage));
                OnPropertyChanged(nameof(BarPercentage));
                OnPropertyChanged(nameof(FullDetails));
            }
        }

        public double? Percentage => NodeSize == null || ParentSize == null ? null : (double?)(NodeSize.Value / (double)Math.Max(1L, ParentSize.Value));
        public int BarPercentage => Percentage == null ? 0 : (int)(Percentage * 100);
        public string DisplayPercentage => Percentage?.ToString("p1");

        public string FullDetails => $"[{DisplaySize} {DisplayPercentage}]";

        public ObservableCollection<FsNode> Children { get; } = new ObservableCollection<FsNode>();

        private bool loaded = false;
        private bool _isExpanded;

        public event PropertyChangedEventHandler PropertyChanged;

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                _isExpanded = value;
                if (_isExpanded && loaded == false)
                {
                    loaded = true;
                    LoadChildren();
                }
            }
        }

        public FsNode(FileSystemInfo fsi)
        {
            if (fsi == null)
            {
                return;
            }
            FileSystemInfo = fsi;
            if (fsi is DirectoryInfo di)
            {
                ImageSource = FolderIcon;
                Children.Add(new FsNode(null));
                //Children.Add(new FsNode(null));
                OnPropertyChanged(nameof(Children));
                DirectoryDetails.GetDirectoryDetails(di).ContinueWith(async s =>
                {
                    DirectoryDetails = s.Result;
                    NodeSize = (await DirectoryDetails.FullSize);
                    OnPropertyChanged(nameof(FullDetails));
                    //.Task.ContinueWith(s => { s.Result; OnPropertyChanged(nameof(FullDetails)); });
                    if (DirectoryDetails.Parent == null)
                    {
                        ParentSize = DriveInfo.GetDrives().Single(s => s.Name == DirectoryDetails.DirectoryInfo.FullName).TotalSize;
                        OnPropertyChanged(nameof(FullDetails));
                    }
                    else
                    {
                        _ = DirectoryDetails.Parent.FullSize.Task.ContinueWith(s => { ParentSize = s.Result; OnPropertyChanged(nameof(FullDetails)); });
                    }
                });
            }
            else if (fsi is FileInfo fi)
            {
                NodeSize = fi.Length;

                DirectoryDetails.GetDirectoryDetails(fi.Directory).ContinueWith(s =>
                {
                    s.Result.FullSize.Task.ContinueWith(s => { ParentSize = s.Result; OnPropertyChanged(nameof(FullDetails)); });
                });

                Task.Run(() =>
                {
                    try
                    {
                        var img = Imaging.CreateBitmapSourceFromHIcon(
                               Icon.ExtractAssociatedIcon(FileSystemInfo.FullName).Handle,
                               Int32Rect.Empty,
                               BitmapSizeOptions.FromEmptyOptions());
                        img.Freeze();
                        ImageSource = img;

                        OnPropertyChanged(nameof(ImageSource));
                    }
                    catch (Exception) { }
                });
            }
        }

        private void LoadChildren()
        {
            if (FileSystemInfo is DirectoryInfo di)
            {
                try
                {
                    DirectoryDetails.SubDirectories.Task.ContinueWith(s =>
                    {
                        var nodes2 = new List<FsNode>();
                        nodes2.AddRange(s.Result.Select(s => s.Value.DirectoryInfo).Select(s => new FsNode(s)));

                        _ = Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            nodes2.ForEach(s => Children.Add(s));
                        }));
                    });
                }
                catch (Exception) { }
                try
                {
                    Children.Clear();
                    di.GetFiles().Select(s => new FsNode(s)).ToList().ForEach(s => Children.Add(s));
                }
                catch (Exception) { }
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            _ = Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }));
        }
    }
}
