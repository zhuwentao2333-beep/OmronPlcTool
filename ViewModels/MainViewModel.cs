using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmronPlcTool.Models;
using OmronPlcTool.Services;
using Opc.Ua;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;

namespace OmronPlcTool.ViewModels;

public enum PlcModel { OmronNJ, KeyenceKVX }

public partial class MainViewModel : ObservableObject
{
    private IPlcCommunication _plc;
    private ICollectionView? _monitoredView;

    public ObservableCollection<ImportCandidate> ImportCandidates { get; } = new();

    public MainViewModel()
    {
        _plc = CreateService();
        WireEvents();
    }

    // ---- Protocol / PLC Model ----

    [ObservableProperty] private string _ipAddress = "192.168.250.1";
    [ObservableProperty] private string _connectionStatus = "未连接";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _selectedProtocolIndex;
    [ObservableProperty] private int _selectedPlcModelIndex;
    [ObservableProperty] private int _plcNodeAddress = 1;
    [ObservableProperty] private string _manualVariableInput = string.Empty;
    [ObservableProperty] private string _searchText = string.Empty;

    public ObservableCollection<TreeNodeModel> TreeRootNodes { get; } = new();
    public ObservableCollection<PlcVariable> MonitoredVariables { get; } = new();
    public ObservableCollection<HierarchicalVar> HierarchicalRoots { get; } = new();

    public List<string> ProtocolNames { get; } = new() { "OPC UA (4840)", "FINS/TCP (9600)", "EtherNet/IP (44818)" };
    public List<string> PlcModelNames { get; } = new() { "欧姆龙 NJ/NX", "基恩士 KV-X" };

    partial void OnSelectedProtocolIndexChanged(int value) => RecreateService();

    partial void OnSelectedPlcModelIndexChanged(int value)
    {
        // Update protocol list based on PLC model
        ProtocolNames.Clear();
        if (value == 0) // Omron
        {
            ProtocolNames.Add("OPC UA (4840)");
            ProtocolNames.Add("FINS/TCP (9600)");
            ProtocolNames.Add("EtherNet/IP (44818)");
        }
        else // Keyence
        {
            ProtocolNames.Add("OPC UA (4840)");
            ProtocolNames.Add("KV Ethernet (8501)");
        }
        SelectedProtocolIndex = 0;
        RecreateService();
    }

    /// <summary>获取当前协议的变量前缀</summary>
    public string NodeIdPrefix
    {
        get
        {
            if (SelectedPlcModelIndex == 1) // Keyence
                return SelectedProtocolIndex == 1 ? "kv:" : "";
            // Omron
            return SelectedProtocolIndex switch { 1 => "fins:", 2 => "eip:", _ => "" };
        }
    }

    /// <summary>为导入的变量生成正确的 NodeId</summary>
    private string MakeNodeId(string variableName)
    {
        var prefix = NodeIdPrefix;
        if (string.IsNullOrEmpty(prefix)) return variableName;
        return prefix + variableName;
    }

    private void RecreateService()
    {
        if (IsConnected) { _plc.Disconnect(); IsConnected = false; TreeRootNodes.Clear(); MonitoredVariables.Clear(); }
        _plc.Dispose();
        _plc = CreateService();
        WireEvents();
    }

    private IPlcCommunication CreateService()
    {
        if (SelectedPlcModelIndex == 1) // Keyence
            return SelectedProtocolIndex switch { 1 => new KeyenceEthernetService(), _ => new OpcUaService() };

        // Omron
        return SelectedProtocolIndex switch
        {
            1 => new FinsService((byte)PlcNodeAddress),
            2 => new EtherNetIpService(),
            _ => new OpcUaService()
        };
    }

    private void WireEvents()
    {
        _plc.ConnectionStatusChanged += status =>
            Application.Current.Dispatcher.Invoke(() => ConnectionStatus = status);

        _plc.TreeBuilt += nodes =>
            Application.Current.Dispatcher.Invoke(() =>
            {
                TreeRootNodes.Clear();
                foreach (var node in nodes) TreeRootNodes.Add(node);
            });

        _plc.ValueChanged += (nodeId, value, timestamp) =>
            Application.Current.Dispatcher.Invoke(() =>
            {
                var flat = MonitoredVariables.FirstOrDefault(v => v.NodeId == nodeId);
                if (flat != null)
                {
                    flat.Value = value;
                    flat.ValueDisplay = value?.ToString() ?? "(null)";
                    flat.Timestamp = timestamp;
                }
                // Also update hierarchical node
                UpdateHierarchicalValue(nodeId, value, timestamp);
            });
    }

    private void UpdateHierarchicalValue(string nodeId, object? value, DateTime timestamp)
    {
        foreach (var root in HierarchicalRoots)
            if (UpdateNodeValue(root, nodeId, value, timestamp))
                break;
    }

    private static bool UpdateNodeValue(HierarchicalVar node, string nodeId, object? value, DateTime ts)
    {
        if (node.NodeId == nodeId)
        {
            node.Value = value;
            node.ValueDisplay = value?.ToString() ?? "(null)";
            node.Timestamp = ts;
            return true;
        }
        foreach (var child in node.Children)
            if (UpdateNodeValue(child, nodeId, value, ts)) return true;
        return false;
    }

    // ---- Connection ----

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnected)
        {
            _plc.Disconnect(); IsConnected = false;
            TreeRootNodes.Clear(); MonitoredVariables.Clear(); HierarchicalRoots.Clear();
            return;
        }
        IsBusy = true; ConnectionStatus = "正在连接...";
        try { await _plc.ConnectAsync(IpAddress); IsConnected = true; }
        catch (Exception ex) { ConnectionStatus = $"连接失败: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    // ---- Tree Browsing ----

    [ObservableProperty] private TreeNodeModel? _selectedTreeNode;

    [RelayCommand]
    private void ExpandNode(TreeNodeModel? node)
    {
        if (node == null || node.IsVariable) return;
        if (node.Children.Count == 0)
            foreach (var child in _plc.BrowseChildren(node.NodeId))
                node.Children.Add(child);
    }

    [RelayCommand]
    private void ImportSelected() => ImportVariablesRecursive(SelectedTreeNode);

    [RelayCommand]
    private void ImportAll()
    {
        foreach (var root in TreeRootNodes) ImportVariablesRecursive(root, true);
    }

    private void ImportVariablesRecursive(TreeNodeModel? node, bool addAll = false)
    {
        if (node == null) return;
        if (node.IsVariable)
        {
            AddToMonitored(node.Name, node.NodeId, null);
            return;
        }
        if (node.Children.Count == 0)
            foreach (var child in _plc.BrowseChildren(node.NodeId))
                node.Children.Add(child);
        if (addAll)
            foreach (var child in node.Children)
                ImportVariablesRecursive(child, true);
    }

    // ---- File Import ----

    [RelayCommand]
    private void ShowImportDialog()
    {
        var imported = PlcVariableImport.FromOpenFileDialog();
        if (imported.Count == 0) return;

        ImportCandidates.Clear();
        var existing = new HashSet<string>(MonitoredVariables.Select(v => v.NodeId));

        foreach (var imp in imported)
        {
            var nodeId = MakeNodeId(imp.Name);
            if (existing.Contains(nodeId)) continue;
            ImportCandidates.Add(new ImportCandidate
            {
                VariableName = imp.Name, NodeId = nodeId,
                DataType = imp.DataType,
                Address = imp.AddressRaw ?? imp.FinsArea ?? "",
                IsSelected = false,
                IsStructMember = imp.Name.Contains('.'),
                IsArrayElement = imp.Name.Contains('[')
            });
        }

        if (ImportCandidates.Count == 0)
        { MessageBox.Show("所有变量已在监控列表中。", "提示"); return; }

        new Views.VariableImportWindow
        {
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        }.ShowDialog();
    }

    [RelayCommand]
    private void ConfirmImport()
    {
        foreach (var cand in ImportCandidates.Where(c => c.IsSelected).ToList())
        {
            object? value = null;
            if (IsConnected) value = _plc.ReadValue(cand.NodeId);
            MonitoredVariables.Add(new PlcVariable
            {
                DisplayName = cand.VariableName, NodeId = cand.NodeId,
                DataType = cand.DataType, Value = value,
                ValueDisplay = value?.ToString() ?? "(null)", Timestamp = DateTime.Now
            });
            ImportCandidates.Remove(cand);
        }
        AfterMonitoredChanged();
    }

    [RelayCommand]
    private void AddManualVariables()
    {
        if (string.IsNullOrWhiteSpace(ManualVariableInput)) return;
        foreach (var imp in PlcVariableImport.FromManualInput(ManualVariableInput))
        {
            var nodeId = MakeNodeId(imp.Name);
            if (MonitoredVariables.Any(v => v.NodeId == nodeId)) continue;
            MonitoredVariables.Add(new PlcVariable
            { DisplayName = imp.Name, NodeId = nodeId, DataType = imp.DataType, ValueDisplay = "(null)", Timestamp = DateTime.Now });
        }
        ManualVariableInput = string.Empty;
        AfterMonitoredChanged();
    }

    // ---- Hierarchy (2b) ----

    [ObservableProperty] private bool _isHierarchicalView;

    partial void OnIsHierarchicalViewChanged(bool value)
    {
        if (value) BuildHierarchy();
        ApplyFlatFilter();
    }

    private void BuildHierarchy()
    {
        HierarchicalRoots.Clear();
        var dict = new Dictionary<string, HierarchicalVar>();

        foreach (var v in MonitoredVariables)
        {
            var segments = HierarchicalVar.ParsePathSegments(v.DisplayName);

            if (segments.Count <= 1)
            {
                var node = new HierarchicalVar
                {
                    DisplayName = v.DisplayName, NodeId = v.NodeId, DataType = v.DataType,
                    Value = v.Value, ValueDisplay = v.ValueDisplay, Timestamp = v.Timestamp,
                    SourceVariable = v, Level = 0
                };
                if (dict.TryAdd(v.DisplayName, node)) HierarchicalRoots.Add(node);
                continue;
            }

            // Build folder chain for all segments except the last
            HierarchicalVar? parent = null;
            var pathSegs = new List<string>();

            for (int i = 0; i < segments.Count - 1; i++)
            {
                pathSegs.Add(segments[i]);
                string currentPath = HierarchicalVar.SegmentsToName(pathSegs);

                if (!dict.TryGetValue(currentPath, out var folder))
                {
                    // Determine if this is an array index folder
                    bool isArrayIndex = segments[i].StartsWith('[');
                    folder = new HierarchicalVar
                    {
                        DisplayName = segments[i],
                        NodeId = $"folder:{currentPath}",
                        DataType = isArrayIndex ? "" : (i == 0 ? "Struct[]" : "Struct"),
                        IsFolder = true,
                        Level = i,
                        IsExpanded = false
                    };
                    dict[currentPath] = folder;
                    if (parent == null) HierarchicalRoots.Add(folder);
                    else parent.Children.Add(folder);
                }
                parent = folder;
            }

            // Last segment = leaf variable
            var fullName = v.DisplayName;
            var leaf = new HierarchicalVar
            {
                DisplayName = segments[^1],
                NodeId = v.NodeId, DataType = v.DataType,
                Value = v.Value, ValueDisplay = v.ValueDisplay, Timestamp = v.Timestamp,
                SourceVariable = v, Level = segments.Count - 1
            };
            parent!.Children.Add(leaf);
            dict[fullName] = leaf;
        }
    }

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var root in HierarchicalRoots) SetExpanded(root, true);
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var root in HierarchicalRoots) SetExpanded(root, false);
    }

    private static void SetExpanded(HierarchicalVar node, bool expanded)
    {
        node.IsExpanded = expanded;
        foreach (var child in node.Children) SetExpanded(child, expanded);
    }

    // ---- Search & Flat View Filter ----

    partial void OnSearchTextChanged(string value) => ApplyFlatFilter();

    private void ApplyFlatFilter()
    {
        if (_monitoredView == null) return;
        _monitoredView.Filter = item =>
        {
            if (item is not PlcVariable v) return false;
            if (string.IsNullOrWhiteSpace(SearchText)) return true;
            return v.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || v.NodeId.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                || v.DataType.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
        };
        _monitoredView.Refresh();
    }

    public void SetMonitoredView(ICollectionView view)
    { _monitoredView = view; ApplyFlatFilter(); }

    // ---- Write ----

    [RelayCommand]
    private void WriteValue(object? parameter)
    {
        if (!IsConnected) return;

        string? nodeId = null;
        string? newValueInput = null;
        Type? targetType = null;

        if (parameter is PlcVariable flat)
        {
            nodeId = flat.NodeId; newValueInput = flat.NewValueInput; targetType = flat.Value?.GetType();
        }
        else if (parameter is HierarchicalVar hier)
        {
            nodeId = hier.NodeId; newValueInput = hier.NewValueInput;
            targetType = hier.SourceVariable?.Value?.GetType() ?? hier.Value?.GetType();
        }

        if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(newValueInput))
        { MessageBox.Show("请输入新值", "提示"); return; }

        try
        {
            _plc.WriteValue(nodeId, ConvertToTargetType(newValueInput, targetType));
            if (parameter is PlcVariable f) f.NewValueInput = string.Empty;
            if (parameter is HierarchicalVar h) h.NewValueInput = string.Empty;
        }
        catch (Exception ex) { MessageBox.Show($"写入失败: {ex.Message}", "错误"); }
    }

    private static object ConvertToTargetType(string input, Type? targetType)
    {
        if (targetType == typeof(bool)) return bool.Parse(input);
        if (targetType == typeof(short)) return short.Parse(input);
        if (targetType == typeof(ushort)) return ushort.Parse(input);
        if (targetType == typeof(int)) return int.Parse(input);
        if (targetType == typeof(uint)) return uint.Parse(input);
        if (targetType == typeof(long)) return long.Parse(input);
        if (targetType == typeof(ulong)) return ulong.Parse(input);
        if (targetType == typeof(float)) return float.Parse(input);
        if (targetType == typeof(double)) return double.Parse(input);
        if (targetType == typeof(byte)) return byte.Parse(input);
        if (targetType == typeof(sbyte)) return sbyte.Parse(input);
        return input;
    }

    // ---- Monitoring ----

    [RelayCommand]
    private void StartMonitoring()
    { _plc.StartMonitoring(MonitoredVariables.Select(v => v.NodeId)); }

    // ---- Delete ----

    [RelayCommand]
    private void BatchDelete(IList<object>? selectedItems)
    {
        if (selectedItems == null || selectedItems.Count == 0) return;
        foreach (var v in selectedItems.Cast<PlcVariable>().ToList())
            RemoveFromMonitored(v);
    }

    [RelayCommand]
    private void RemoveVariable(object? parameter)
    {
        if (parameter is PlcVariable flat) RemoveFromMonitored(flat);
        else if (parameter is HierarchicalVar hier && hier.SourceVariable != null) RemoveFromMonitored(hier.SourceVariable);
    }

    private void RemoveFromMonitored(PlcVariable v)
    {
        MonitoredVariables.Remove(v);
        ImportCandidates.Add(new ImportCandidate
        { VariableName = v.DisplayName, NodeId = v.NodeId, DataType = v.DataType, IsSelected = false });
        AfterMonitoredChanged();
    }

    // ---- Helpers ----

    private void AddToMonitored(string name, string nodeId, object? value)
    {
        if (MonitoredVariables.Any(v => v.NodeId == nodeId)) return;
        MonitoredVariables.Add(new PlcVariable
        {
            DisplayName = name, NodeId = nodeId,
            DataType = value?.GetType().Name ?? "Unknown",
            Value = value, ValueDisplay = value?.ToString() ?? "(null)", Timestamp = DateTime.Now
        });
        AfterMonitoredChanged();
    }

    private void AfterMonitoredChanged()
    {
        ApplyFlatFilter();
        if (IsHierarchicalView) BuildHierarchy();
    }
}
