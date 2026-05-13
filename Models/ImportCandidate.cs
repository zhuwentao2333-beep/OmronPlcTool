using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace OmronPlcTool.Models;

/// <summary>
/// 变量导入选择池中的候选项——文件导入后、确认加入监控表之前的中间状态。
/// </summary>
public partial class ImportCandidate : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public string VariableName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public bool IsStructMember { get; set; }
    public bool IsArrayElement { get; set; }
    public bool IsFolder { get; set; }
    public ObservableCollection<ImportCandidate> Children { get; set; } = new();
}
