using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace OmronPlcTool.Models;

/// <summary>
/// 变量树节点 — 支持结构体/数组的层级展开折叠显示
/// </summary>
public partial class HierarchicalVar : ObservableObject
{
    public string DisplayName { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public object? Value { get; set; }

    [ObservableProperty]
    private string _valueDisplay = string.Empty;

    [ObservableProperty]
    private DateTime _timestamp;

    [ObservableProperty]
    private string _newValueInput = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    public bool IsFolder { get; set; }
    public int Level { get; set; }
    public string ParentKey { get; set; } = string.Empty;
    public ObservableCollection<HierarchicalVar> Children { get; set; } = new();
    public PlcVariable? SourceVariable { get; set; }

    /// <summary>
    /// 将变量名解析为路径段。
    /// 例如 StructArray[3].Member.Sub → ["StructArray", "[3]", "Member", "Sub"]
    /// </summary>
    public static List<string> ParsePathSegments(string name)
    {
        var segments = new List<string>();
        int i = 0;
        while (i < name.Length)
        {
            int bracketIdx = name.IndexOf('[', i);
            int dotIdx = name.IndexOf('.', i);

            // 数组索引优先于结构体点号
            if (bracketIdx >= 0 && (dotIdx < 0 || bracketIdx < dotIdx))
            {
                if (bracketIdx > i)
                    segments.Add(name[i..bracketIdx]);

                int closeBracket = name.IndexOf(']', bracketIdx);
                if (closeBracket >= 0)
                {
                    segments.Add(name[bracketIdx..(closeBracket + 1)]); // "[0]"
                    i = closeBracket + 1;
                    if (i < name.Length && name[i] == '.') i++;
                }
                else
                {
                    i = bracketIdx + 1;
                }
            }
            else if (dotIdx >= 0)
            {
                if (dotIdx > i)
                    segments.Add(name[i..dotIdx]);
                i = dotIdx + 1;
            }
            else
            {
                segments.Add(name[i..]);
                break;
            }
        }
        return segments;
    }

    /// <summary>从路径段列表重建完整变量名</summary>
    public static string SegmentsToName(List<string> segments)
    {
        if (segments.Count == 0) return "";
        var sb = new System.Text.StringBuilder(segments[0]);
        for (int i = 1; i < segments.Count; i++)
        {
            if (segments[i].StartsWith('['))
                sb.Append(segments[i]);
            else
                sb.Append('.').Append(segments[i]);
        }
        return sb.ToString();
    }
}
