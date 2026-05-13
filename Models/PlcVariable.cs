using CommunityToolkit.Mvvm.ComponentModel;

namespace OmronPlcTool.Models;

public partial class PlcVariable : ObservableObject
{
    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _nodeId = string.Empty;

    [ObservableProperty]
    private string _dataType = string.Empty;

    [ObservableProperty]
    private object? _value;

    [ObservableProperty]
    private string _valueDisplay = string.Empty;

    [ObservableProperty]
    private DateTime _timestamp;

    [ObservableProperty]
    private string _newValueInput = string.Empty;
}
