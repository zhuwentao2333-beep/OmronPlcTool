using Microsoft.Win32;
using System.IO;
using System.Text;

namespace OmronPlcTool.Services;

/// <summary>
/// 通过 CSV/TSV/TXT 导入 PLC 变量定义。
/// 自动检测编码 (UTF-8/UTF-16/ANSI/GB2312/Shift-JIS) 和分隔符 (逗号/Tab)。
/// 支持 Sysmac Studio、KV Studio 等主流 PLC 编程软件导出的变量表。
/// </summary>
public static class PlcVariableImport
{
    public record ImportedVariable(string Name, string DataType, string? FinsArea, int? FinsAddress, string? AddressRaw);

    public static List<ImportedVariable> FromOpenFileDialog(string title = "导入变量列表")
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "变量表文件 (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|CSV 文件 (*.csv)|*.csv|TXT 文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return new List<ImportedVariable>();

        return FromVariableFile(dialog.FileName);
    }

    public static List<ImportedVariable> FromVariableFile(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var (lines, encodingUsed) = ReadAllLinesWithEncoding(bytes);

        if (lines.Length == 0) return new List<ImportedVariable>();

        return ParseLines(lines);
    }

    // ---- Encoding Detection ----

    private static (string[] lines, Encoding encoding) ReadAllLinesWithEncoding(byte[] bytes)
    {
        // Register code pages provider for ANSI encodings (GB2312, Shift-JIS, etc.)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var candidates = new List<Encoding>
        {
            Encoding.Default,   // System ANSI
            Encoding.UTF8,
            Encoding.Unicode    // UTF-16 LE
        };

        try { candidates.Add(Encoding.GetEncoding(936)); } catch { }  // GBK/GB2312
        try { candidates.Add(Encoding.GetEncoding(932)); } catch { }  // Shift-JIS
        try { candidates.Add(Encoding.GetEncoding(54936)); } catch { } // GB18030
        try { candidates.Add(Encoding.GetEncoding(20936)); } catch { } // GB2312 alt

        (string[] lines, Encoding encoding) best = (Array.Empty<string>(), Encoding.Default);
        int bestScore = int.MinValue;

        foreach (var enc in candidates.DistinctBy(e => e.CodePage))
        {
            try
            {
                var text = enc.GetString(bytes);
                var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
                                .Where(l => !string.IsNullOrWhiteSpace(l))
                                .ToArray();

                if (lines.Length == 0) continue;

                int score = ScoreLines(lines, enc);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = (lines, enc);
                }
            }
            catch { continue; }
        }

        return best;
    }

    private static int ScoreLines(string[] lines, Encoding enc)
    {
        int score = 0;
        var header = lines[0];

        string[] keyTerms =
        {
            // 列头关键词 — 中/英/日
            "NAME", "名称", "変数名", "变量名", "Name",
            "DATATYPE", "数据类型", "データ型", "Data Type", "型名",
            "ADDRESS", "AT", "地址", "アドレス", "AT指定",
            "COMMENT", "注释", "コメント",
            "HOST", "TAGLINK", "PO",
            // 常见数据类型
            "BOOL", "INT", "DINT", "REAL", "LREAL", "WORD", "STRING", "UINT"
        };

        foreach (var term in keyTerms)
        {
            if (header.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 10;
        }

        // Data type terms in body lines (not just header)
        foreach (var line in lines.Skip(1).Take(10))
        {
            foreach (var dt in new[] { "BOOL", "INT", "DINT", "REAL", "LREAL", "WORD", "STRING", "UINT", "UDINT", "TRUE", "FALSE" })
            {
                if (line.Contains(dt, StringComparison.OrdinalIgnoreCase))
                    score += 2;
            }
        }

        // BOM bonus
        if (enc.GetPreamble().Length > 0)
            score += 5;

        // Replacement char penalty
        int replacementCount = 0;
        foreach (var line in lines.Take(5))
            replacementCount += line.Count(c => c == '�');
        score -= replacementCount * 20;

        return score;
    }

    // ---- Delimiter Detection & Parsing ----

    private static char DetectDelimiter(string header)
    {
        int tabCount = header.Count(c => c == '\t');
        int commaCount = header.Count(c => c == ',');

        // Sysmac Studio TSV: header like "HOST\tNAME\tDATATYPE\tADDRESS\tCOMMENT\tTAGLINK\tRW\tPOU"
        // KV Studio CSV:    header like "名称,数据类型,地址,注释,..."
        // General CSV:      header like "Name,Data Type,Address,Comment,..."
        if (tabCount >= 2 && tabCount >= commaCount)
            return '\t';
        if (commaCount >= 2)
            return ',';

        // Fallback: try tab, then comma
        return tabCount > 0 ? '\t' : ',';
    }

    private static string[] SplitLine(string line, char delimiter)
    {
        var result = new List<string>();
        bool inQuotes = false;
        int start = 0;

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (line[i] == delimiter && !inQuotes)
            {
                result.Add(line[start..i]);
                start = i + 1;
            }
        }
        result.Add(line[start..]);

        return result.Select(s => s.Trim().Trim('"')).ToArray();
    }

    // ---- Main Parsing Logic ----

    private static List<ImportedVariable> ParseLines(string[] lines)
    {
        var variables = new List<ImportedVariable>();

        var header = lines[0];
        var delimiter = DetectDelimiter(header);
        var columns = SplitLine(header, delimiter);

        int nameIdx = -1, typeIdx = -1, atIdx = -1, commentIdx = -1;

        for (int i = 0; i < columns.Length; i++)
        {
            var col = columns[i].Trim().Trim('﻿', '​');

            if (col is "NAME" or "名称" or "変数名" or "变量名" or "Name")
                nameIdx = i;
            else if (col is "DATATYPE" or "数据类型" or "データ型" or "Data Type" or "型名")
                typeIdx = i;
            else if (col is "ADDRESS" or "AT" or "地址" or "アドレス" or "AT指定" or "AT 指定" or "AT specification")
                atIdx = i;
            else if (col is "COMMENT" or "注释" or "コメント" or "Comment")
                commentIdx = i;
        }

        // Fallback: broader matching
        if (nameIdx < 0)
        {
            for (int i = 0; i < columns.Length; i++)
            {
                var col = columns[i].Trim().Trim('﻿', '​', '"');
                if (col.Contains("名", StringComparison.Ordinal) || col.StartsWith("Name", StringComparison.OrdinalIgnoreCase) || col.StartsWith("NAME", StringComparison.Ordinal))
                    nameIdx = i;
                if (col.Contains("型", StringComparison.Ordinal) || col.StartsWith("DATA", StringComparison.OrdinalIgnoreCase) || col.StartsWith("Type", StringComparison.OrdinalIgnoreCase))
                    typeIdx = i;
                if (col.StartsWith("ADDR", StringComparison.OrdinalIgnoreCase) || col.Contains("地址") || col.Contains("AT", StringComparison.Ordinal))
                    atIdx = i;
            }
        }

        // Desperate fallback: SYSMAC TSV always has NAME at index 1, DATATYPE at index 2, ADDRESS at index 3
        if (nameIdx < 0 && columns.Length >= 2) nameIdx = 1;
        if (typeIdx < 0 && columns.Length >= 3) typeIdx = 2;
        if (atIdx < 0 && columns.Length >= 4) atIdx = 3;

        // Parse data lines
        for (int lineIdx = 1; lineIdx < lines.Length; lineIdx++)
        {
            var line = lines[lineIdx];
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith('#') || line.StartsWith("//")) continue;

            var values = SplitLine(line, delimiter);
            if (values.Length == 0) continue;

            var name = nameIdx >= 0 && nameIdx < values.Length ? values[nameIdx].Trim() : "";
            var dataType = typeIdx >= 0 && typeIdx < values.Length ? values[typeIdx].Trim() : "";
            var addressRaw = atIdx >= 0 && atIdx < values.Length ? values[atIdx].Trim() : "";

            if (string.IsNullOrWhiteSpace(name)) continue;

            // Parse AT / ADDRESS
            string? finsArea = null;
            int? finsAddress = null;
            if (!string.IsNullOrWhiteSpace(addressRaw))
            {
                (finsArea, finsAddress) = ParseAddress(addressRaw);
            }

            variables.Add(new ImportedVariable(name, dataType, finsArea, finsAddress, addressRaw));
        }

        return variables;
    }

    public static List<ImportedVariable> FromManualInput(string text)
    {
        var variables = new List<ImportedVariable>();
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            // Support both comma and tab as delimiter in manual input
            var parts = line.Contains('\t') ? line.Split('\t') : line.Split(',');
            var name = parts[0].Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var dataType = parts.Length > 1 ? parts[1].Trim() : "";
            variables.Add(new ImportedVariable(name, dataType, null, null, null));
        }

        return variables;
    }

    // ---- Address Parsing ----

    /// <summary>
    /// Parse address field from various PLC formats:
    ///   %D100, %W0, %H5, %R0  (FINS memory area)
    ///   ECAT://node#[15,0]/...  (EtherCAT — no simple FINS mapping)
    ///   %IW0.0, %QW0  (I/O mapped)
    /// </summary>
    public static (string? area, int? address) ParseAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return (null, null);

        // Skip complex addresses (EtherCAT, device paths)
        if (address.Contains("://") || address.StartsWith("ECAT", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        var original = address.Trim();

        // Handle % prefix (Sysmac Studio style)
        if (original.StartsWith('%'))
        {
            original = original[1..];
        }

        if (original.Length < 2) return (null, null);

        // Extract area letters
        int i = 0;
        while (i < original.Length && char.IsLetter(original[i])) i++;
        if (i == 0) return (null, null);

        var area = original[..i];
        var addrStr = original[i..];

        // Handle bit-level addressing: D100.0 → D:100
        int dotIdx = addrStr.IndexOf('.');
        if (dotIdx >= 0) addrStr = addrStr[..dotIdx];

        if (int.TryParse(addrStr, out int addr))
        {
            var finsArea = area.ToUpperInvariant() switch
            {
                "R" or "CIO" => "cio",
                "W" => "w",
                "H" => "h",
                "D" => "d",
                "E" or "EM" => "em",
                _ => area.ToLowerInvariant()
            };
            return (finsArea, addr);
        }

        return (null, null);
    }

    public static string ToNodeId(ImportedVariable variable)
    {
        if (!string.IsNullOrEmpty(variable.FinsArea) && variable.FinsAddress.HasValue)
            return $"fins:{variable.FinsArea}:{variable.FinsAddress.Value}";

        return $"fins:{variable.Name}";
    }
}
