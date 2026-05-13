using OmronPlcTool.Services;
using OmronPlcTool.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace OmronPlcTool.Views;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Title = $"Omron/Keyence PLC Tool v{ver?.Major}.{ver?.Minor}.{ver?.Build}";
    }

    private void VariablesGrid_Loaded(object sender, RoutedEventArgs e)
    {
        var view = CollectionViewSource.GetDefaultView(ViewModel.MonitoredVariables);
        ViewModel.SetMonitoredView(view);
    }

    private void VariablesGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && VariablesGrid.SelectedItems.Count > 0)
        {
            var selected = VariablesGrid.SelectedItems.Cast<object>().ToList();
            ViewModel.BatchDeleteCommand.Execute(selected);
            e.Handled = true;
        }
    }

    private void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem item && item.DataContext is TreeNodeModel node)
        {
            ViewModel.ExpandNodeCommand.Execute(node);
        }
    }

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeNodeModel node)
        {
            ViewModel.SelectedTreeNode = node;
        }
    }
}
