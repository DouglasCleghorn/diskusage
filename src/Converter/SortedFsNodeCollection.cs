using diskusage.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Data;
using System.ComponentModel;

namespace diskusage.Converter
{
    public class SortedFsNodeCollection : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var src = value as ObservableCollection<FsNode>;
            var view = CollectionViewSource.GetDefaultView(src) as ListCollectionView;
            view.LiveSortingProperties.Add("NodeSize");
            view.SortDescriptions.Add(new SortDescription("NodeSize", ListSortDirection.Descending));
            view.IsLiveSorting = true;
            return view;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
