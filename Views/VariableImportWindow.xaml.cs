using OmronPlcTool.Models;
using OmronPlcTool.ViewModels;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace OmronPlcTool.Views;

public partial class VariableImportWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;
    private ICollectionView? _flatView;
    private readonly ObservableCollection<ImportCandidate> _hierarchicalRoots = new();

    public VariableImportWindow()
    {
        InitializeComponent();
        DataContext = ((MainWindow)Application.Current.MainWindow).DataContext;

        Loaded += (_, _) =>
        {
            _flatView = CollectionViewSource.GetDefaultView(Vm.ImportCandidates);
            _flatView.Filter = FlatFilter;
            BuildHierarchy();
            UpdateSelectionCount();
            Vm.ImportCandidates.CollectionChanged += (_, _) =>
            {
                UpdateSelectionCount();
                if (HierarchyCheck.IsChecked == true) BuildHierarchy();
            };
        };
    }

    // ---- Flat view filtering ----

    private bool FlatFilter(object obj)
    {
        if (obj is not ImportCandidate c) return false;
        if (ShowStructOnly.IsChecked == true && !c.IsStructMember && !c.IsArrayElement) return false;
        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            var t = SearchBox.Text;
            return c.VariableName.Contains(t, StringComparison.OrdinalIgnoreCase)
                || c.DataType.Contains(t, StringComparison.OrdinalIgnoreCase)
                || c.Address.Contains(t, StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    // ---- Hierarchy ----

    private void BuildHierarchy()
    {
        _hierarchicalRoots.Clear();
        var dict = new Dictionary<string, ImportCandidate>();

        foreach (var c in Vm.ImportCandidates)
        {
            var segments = HierarchicalVar.ParsePathSegments(c.VariableName);

            if (segments.Count <= 1)
            {
                c.DisplayName = c.VariableName;
                c.IsFolder = false;
                c.Children.Clear();
                dict.TryAdd(c.VariableName, c);
                _hierarchicalRoots.Add(c);
                continue;
            }

            // Build folder chain for all segments except the last
            ImportCandidate? parent = null;
            var pathSegs = new List<string>();

            for (int i = 0; i < segments.Count - 1; i++)
            {
                pathSegs.Add(segments[i]);
                string currentPath = HierarchicalVar.SegmentsToName(pathSegs);
                if (!dict.TryGetValue(currentPath, out var folder))
                {
                    folder = new ImportCandidate
                    {
                        VariableName = currentPath,
                        DisplayName = segments[i],
                        IsFolder = true,
                        IsSelected = false
                    };
                    dict[currentPath] = folder;
                    if (parent == null) _hierarchicalRoots.Add(folder);
                    else parent.Children.Add(folder);
                }
                parent = folder;
            }

            var leaf = new ImportCandidate
            {
                VariableName = c.VariableName,
                DisplayName = segments[^1],
                NodeId = c.NodeId,
                DataType = c.DataType,
                Address = c.Address,
                IsFolder = false,
                IsSelected = c.IsSelected
            };
            parent!.Children.Add(leaf);
            dict[c.VariableName] = leaf;
        }
    }

    private void HierarchyToggled(object sender, RoutedEventArgs e)
    {
        bool hier = HierarchyCheck.IsChecked == true;
        FlatGrid.Visibility = hier ? Visibility.Collapsed : Visibility.Visible;
        HierarchyTree.Visibility = hier ? Visibility.Visible : Visibility.Collapsed;
        HierarchyBar.Visibility = hier ? Visibility.Visible : Visibility.Collapsed;

        if (hier)
        {
            BuildHierarchy();
            HierarchyTree.ItemsSource = _hierarchicalRoots;
        }
        else
        {
            HierarchyTree.ItemsSource = null;
        }
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
        => SetAllExpanded(_hierarchicalRoots, true);

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
        => SetAllExpanded(_hierarchicalRoots, false);

    private static void SetAllExpanded(ObservableCollection<ImportCandidate> nodes, bool expanded)
    {
        foreach (var n in nodes)
        {
            // Do nothing for now; TreeViewItem.IsExpanded is handled via style
        }
    }

    private void SelectParents_Click(object sender, RoutedEventArgs e)
    {
        foreach (var root in _hierarchicalRoots)
            SyncSelection(root);
        SyncFlatFromHierarchy();
        UpdateSelectionCount();
    }

    private static void SyncSelection(ImportCandidate node)
    {
        if (!node.IsFolder) return;
        bool allChildrenSelected = node.Children.Count > 0 && node.Children.All(c => c.IsSelected);
        node.IsSelected = allChildrenSelected;
        foreach (var child in node.Children.Where(c => c.IsFolder))
            SyncSelection(child);
    }

    private void SyncFlatFromHierarchy()
    {
        foreach (var node in _hierarchicalRoots)
            SyncFlatNode(node);
    }

    private void SyncFlatNode(ImportCandidate node)
    {
        if (!node.IsFolder)
        {
            var flat = Vm.ImportCandidates.FirstOrDefault(c => c.VariableName == node.VariableName);
            if (flat != null) flat.IsSelected = node.IsSelected;
        }
        foreach (var child in node.Children) SyncFlatNode(child);
    }

    // ---- Selection ----

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in Vm.ImportCandidates) c.IsSelected = true;
        UpdateSelectionCount();
    }

    private void DeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in Vm.ImportCandidates) c.IsSelected = false;
        UpdateSelectionCount();
    }

    private void Invert_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in Vm.ImportCandidates) c.IsSelected = !c.IsSelected;
        UpdateSelectionCount();
    }

    private void UpdateSelectionCount()
    {
        var count = Vm.ImportCandidates.Count(c => c.IsSelected);
        SelectedCount.Text = $"已选 {count} 个变量";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => _flatView?.Refresh();

    private void FilterChanged(object sender, RoutedEventArgs e)
        => _flatView?.Refresh();
}
