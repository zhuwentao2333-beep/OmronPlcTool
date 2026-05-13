using Opc.Ua;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace OmronPlcTool.Services;

public class FinsService : IPlcCommunication
{
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private byte _sid;
    private readonly byte _da1; // PLC node address
    private const int FinsPort = 9600;

    public bool IsConnected => _tcpClient?.Connected == true && _stream != null;

    public event Action<string>? ConnectionStatusChanged;
    public event Action<List<TreeNodeModel>>? TreeBuilt;
    public event Action<string, object?, DateTime>? ValueChanged;

    // nodeAddress: PLC FINS node number, default to IP last octet
    public FinsService(byte nodeAddress = 1)
    {
        _da1 = nodeAddress;
    }

    public async Task ConnectAsync(string ipAddress)
    {
        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(ipAddress, FinsPort);
        _stream = _tcpClient.GetStream();
        ConnectionStatusChanged?.Invoke($"FINS 已连接: {ipAddress}:{FinsPort} (节点 {_da1})");

        await Task.Run(() =>
        {
            var rootNodes = new List<TreeNodeModel>
            {
                new()
                {
                    Name = "全局变量 (手动输入/CSV导入)",
                    NodeId = "fins:variables",
                    NodeClass = NodeClass.Object
                },
                new()
                {
                    Name = "CIO 区 (0x30)",
                    NodeId = "fins:cio",
                    NodeClass = NodeClass.Object
                },
                new()
                {
                    Name = "Work 区 / W (0x31)",
                    NodeId = "fins:w",
                    NodeClass = NodeClass.Object
                },
                new()
                {
                    Name = "Holding Relay / H (0x32)",
                    NodeId = "fins:h",
                    NodeClass = NodeClass.Object
                },
                new()
                {
                    Name = "DM 区 / D (0x82)",
                    NodeId = "fins:d",
                    NodeClass = NodeClass.Object
                }
            };
            TreeBuilt?.Invoke(rootNodes);
        });
    }

    public void Disconnect()
    {
        _stream?.Close();
        _tcpClient?.Close();
        _stream = null;
        _tcpClient = null;
        ConnectionStatusChanged?.Invoke("FINS 未连接");
    }

    public List<TreeNodeModel> BrowseChildren(string parentNodeId)
    {
        // For FINS, return address-based children for known memory areas
        var children = new List<TreeNodeModel>();
        for (int addr = 0; addr < 100; addr++)
        {
            children.Add(new TreeNodeModel
            {
                Name = $"{parentNodeId}:{addr:D4}",
                NodeId = $"{parentNodeId}:{addr}",
                NodeClass = NodeClass.Variable
            });
        }
        return children;
    }

    public object? ReadValue(string nodeId)
    {
        if (!IsConnected) return null;

        try
        {
            // Parse: "fins:variableName" for variable access,
            // or "fins:areaCode:address" for direct memory access
            if (nodeId.StartsWith("fins:"))
            {
                var parts = nodeId.Split(':');
                if (parts.Length == 2)
                {
                    // Variable name access using FINS 0x21 command
                    return ReadVariableByName(parts[1]);
                }
                if (parts.Length == 3)
                {
                    // Memory area access
                    byte areaCode = GetAreaCode(parts[1]);
                    ushort addr = ushort.Parse(parts[2]);
                    return ReadMemoryArea(areaCode, addr);
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            ConnectionStatusChanged?.Invoke($"FINS 读取失败: {ex.Message}");
            return null;
        }
    }

    public void WriteValue(string nodeId, object value)
    {
        if (!IsConnected) return;

        try
        {
            if (nodeId.StartsWith("fins:"))
            {
                var parts = nodeId.Split(':');
                if (parts.Length == 2)
                {
                    WriteVariableByName(parts[1], value);
                    return;
                }
                if (parts.Length == 3)
                {
                    byte areaCode = GetAreaCode(parts[1]);
                    ushort addr = ushort.Parse(parts[2]);
                    WriteMemoryArea(areaCode, addr, value);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            ConnectionStatusChanged?.Invoke($"FINS 写入失败: {ex.Message}");
        }
    }

    public void StartMonitoring(IEnumerable<string> nodeIds, int samplingInterval = 500)
    {
        _ = Task.Run(async () =>
        {
            while (IsConnected)
            {
                foreach (var nodeId in nodeIds)
                {
                    var value = ReadValue(nodeId);
                    ValueChanged?.Invoke(nodeId, value, DateTime.Now);
                }
                await Task.Delay(samplingInterval);
            }
        });
    }

    // ---- FINS Protocol Low-Level ----

    private byte[] ReadWriteFrame(byte[] finsCommand, int expectedResponseSize)
    {
        if (_stream == null) throw new InvalidOperationException("Not connected");

        _sid = (byte)((_sid % 255) + 1);

        // Build FINS header
        var finsHeader = new byte[10];
        finsHeader[0] = 0x80;  // ICF: response required
        finsHeader[1] = 0x00;  // RSV
        finsHeader[2] = 0x02;  // GCT
        finsHeader[3] = 0x00;  // DNA (local network)
        finsHeader[4] = _da1;  // DA1 (PLC node)
        finsHeader[5] = 0x00;  // DA2 (CPU unit)
        finsHeader[6] = 0x00;  // SNA (local network)
        finsHeader[7] = 0xFE;  // SA1 (PC node)
        finsHeader[8] = 0x00;  // SA2
        finsHeader[9] = _sid;  // SID

        // Combine FINS header + command
        var finsFrame = new byte[finsHeader.Length + finsCommand.Length];
        Buffer.BlockCopy(finsHeader, 0, finsFrame, 0, finsHeader.Length);
        Buffer.BlockCopy(finsCommand, 0, finsFrame, finsHeader.Length, finsCommand.Length);

        // Build FINS/TCP frame
        var frame = new byte[16 + finsFrame.Length];
        // "FINS" magic
        frame[0] = 0x46; frame[1] = 0x49; frame[2] = 0x4E; frame[3] = 0x53;
        // Length (big-endian): 8 + finsFrame.Length
        int length = 8 + finsFrame.Length;
        frame[4] = (byte)(length >> 24);
        frame[5] = (byte)(length >> 16);
        frame[6] = (byte)(length >> 8);
        frame[7] = (byte)length;
        // Command: 0x00000002 (data send)
        frame[8] = frame[9] = frame[10] = 0x00;
        frame[11] = 0x02;
        // Error: 0x00000000
        frame[12] = frame[13] = frame[14] = frame[15] = 0x00;

        // Full frame = FINS/TCP header + FINS frame
        var fullFrame = new byte[16 + finsFrame.Length];
        Buffer.BlockCopy(frame, 0, fullFrame, 0, 16);
        Buffer.BlockCopy(finsFrame, 0, fullFrame, 16, finsFrame.Length);

        _stream.Write(fullFrame, 0, fullFrame.Length);
        _stream.Flush();

        // Read response (FINS/TCP header + FINS response)
        var responseBuffer = new byte[16 + expectedResponseSize];
        int totalRead = 0;
        while (totalRead < responseBuffer.Length)
        {
            int read = _stream.Read(responseBuffer, totalRead, responseBuffer.Length - totalRead);
            if (read == 0) throw new SocketException();
            totalRead += read;
        }

        // Verify FINS/TCP header
        if (responseBuffer[0] != 0x46 || responseBuffer[1] != 0x49 ||
            responseBuffer[2] != 0x4E || responseBuffer[3] != 0x53)
            throw new Exception("Invalid FINS/TCP response");

        // Check response SID matches
        if (responseBuffer[25] != _sid)
            throw new Exception("FINS response SID mismatch");

        // Extract FINS response (after 16-byte FINS/TCP header + 10-byte FINS header)
        int responseDataLen = expectedResponseSize - 10;
        var response = new byte[responseDataLen];
        Buffer.BlockCopy(responseBuffer, 26, response, 0, responseDataLen);
        return response;
    }

    // ---- Memory Area Read/Write ----

    private object? ReadMemoryArea(byte areaCode, ushort address)
    {
        try
        {
            // FINS command 0101: Memory Area Read
            var cmd = new byte[5];
            cmd[0] = 0x01; cmd[1] = 0x01; // Command 0101
            cmd[2] = areaCode;
            cmd[3] = (byte)(address >> 8);
            cmd[4] = (byte)address;
            // Item count: 1 word

            var response = ReadWriteFrame(cmd, 10 + 2); // 10(header) + 2(data for 1 word)

            // Parse response: bytes 0-1 = response code, bytes 2-3 = data (1 word)
            ushort respCode = (ushort)((response[0] << 8) | response[1]);
            if (respCode != 0)
                throw new Exception($"FINS read error: 0x{respCode:X4}");

            return (ushort)((response[2] << 8) | response[3]);
        }
        catch
        {
            // Fallback: try variable name access
            return null;
        }
    }

    private void WriteMemoryArea(byte areaCode, ushort address, object value)
    {
        // FINS command 0102: Memory Area Write
        var cmd = new byte[7];
        cmd[0] = 0x01; cmd[1] = 0x02; // Command 0102
        cmd[2] = areaCode;
        cmd[3] = (byte)(address >> 8);
        cmd[4] = (byte)address;
        cmd[5] = 0x00; cmd[6] = 0x01; // 1 item

        ushort val = Convert.ToUInt16(value);
        // Append data
        Array.Resize(ref cmd, 9);
        cmd[7] = (byte)(val >> 8);
        cmd[8] = (byte)val;

        var response = ReadWriteFrame(cmd, 10 + 2);
        ushort respCode = (ushort)((response[0] << 8) | response[1]);
        if (respCode != 0)
            throw new Exception($"FINS write error: 0x{respCode:X4}");
    }

    // ---- Variable Name Access (FINS 0x21) ----

    private object? ReadVariableByName(string variableName)
    {
        // FINS command 2101: Variable Read
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(variableName);
        var cmd = new byte[4 + nameBytes.Length];
        cmd[0] = 0x21; cmd[1] = 0x01; // Command 2101
        cmd[2] = (byte)(nameBytes.Length >> 8);
        cmd[3] = (byte)nameBytes.Length;
        Buffer.BlockCopy(nameBytes, 0, cmd, 4, nameBytes.Length);

        var response = ReadWriteFrame(cmd, 10 + 256); // Max 256 bytes response

        ushort respCode = (ushort)((response[0] << 8) | response[1]);
        if (respCode != 0)
            throw new Exception($"FINS variable read error: 0x{respCode:X4}");

        // Parse data type and value
        ushort dataType = (ushort)((response[2] << 8) | response[3]);
        ushort dataLen = (ushort)((response[4] << 8) | response[5]);
        var data = new byte[dataLen];
        Buffer.BlockCopy(response, 6, data, 0, dataLen);

        return ParseFinsValue(dataType, data);
    }

    private void WriteVariableByName(string variableName, object value)
    {
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(variableName);
        var valueBytes = EncodeFinsValue(value, out ushort dataType);

        var cmd = new byte[4 + nameBytes.Length + 4 + valueBytes.Length];
        cmd[0] = 0x21; cmd[1] = 0x02; // Command 2102
        cmd[2] = (byte)(nameBytes.Length >> 8);
        cmd[3] = (byte)nameBytes.Length;
        Buffer.BlockCopy(nameBytes, 0, cmd, 4, nameBytes.Length);

        int offset = 4 + nameBytes.Length;
        cmd[offset] = (byte)(dataType >> 8);
        cmd[offset + 1] = (byte)dataType;
        cmd[offset + 2] = (byte)(valueBytes.Length >> 8);
        cmd[offset + 3] = (byte)valueBytes.Length;
        Buffer.BlockCopy(valueBytes, 0, cmd, offset + 4, valueBytes.Length);

        var response = ReadWriteFrame(cmd, 10 + 4);
        ushort respCode = (ushort)((response[0] << 8) | response[1]);
        if (respCode != 0)
            throw new Exception($"FINS variable write error: 0x{respCode:X4}");
    }

    // ---- Helpers ----

    private static byte GetAreaCode(string area)
    {
        return area.ToLower() switch
        {
            "cio" => 0x30,
            "w" or "work" => 0x31,
            "h" or "holding" => 0x32,
            "d" or "dm" => 0x82,
            _ => throw new ArgumentException($"Unknown memory area: {area}")
        };
    }

    private static object? ParseFinsValue(ushort dataType, byte[] data)
    {
        return dataType switch
        {
            0x0001 => data[0] != 0,                          // BOOL
            0x0002 => (short)((data[0] << 8) | data[1]),      // INT
            0x0003 => (int)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]), // DINT
            0x0004 => (long)((long)data[0] << 56 | (long)data[1] << 48 | (long)data[2] << 40 | (long)data[3] << 32 | (long)data[4] << 24 | (long)data[5] << 16 | (long)data[6] << 8 | data[7]), // LINT
            0x0005 => (ushort)((data[0] << 8) | data[1]),    // UINT
            0x0006 => (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]),  // UDINT
            0x0007 => (ulong)((ulong)data[0] << 56 | (ulong)data[1] << 48 | (ulong)data[2] << 40 | (ulong)data[3] << 32 | (ulong)data[4] << 24 | (ulong)data[5] << 16 | (ulong)data[6] << 8 | data[7]), // ULINT
            0x000B => BitConverter.ToSingle(new[] { data[1], data[0], data[3], data[2] }, 0), // REAL (float)
            0x000C => BitConverter.ToDouble(new[] { data[7], data[6], data[5], data[4], data[3], data[2], data[1], data[0] }, 0), // LREAL
            0x000D => System.Text.Encoding.ASCII.GetString(data).TrimEnd('\0'), // STRING
            0x0012 => data[0],                                  // USINT
            0x0011 => (sbyte)data[0],                          // SINT
            _ => data
        };
    }

    private static byte[] EncodeFinsValue(object value, out ushort dataType)
    {
        switch (value)
        {
            case bool b:
                dataType = 0x0001;
                return new byte[] { (byte)(b ? 1 : 0) };
            case short s:
                dataType = 0x0002;
                return new[] { (byte)(s >> 8), (byte)s };
            case int i:
                dataType = 0x0003;
                return new[] { (byte)(i >> 24), (byte)(i >> 16), (byte)(i >> 8), (byte)i };
            case long l:
                dataType = 0x0004;
                return new[] { (byte)(l >> 56), (byte)(l >> 48), (byte)(l >> 40), (byte)(l >> 32), (byte)(l >> 24), (byte)(l >> 16), (byte)(l >> 8), (byte)l };
            case ushort us:
                dataType = 0x0005;
                return new[] { (byte)(us >> 8), (byte)us };
            case uint ui:
                dataType = 0x0006;
                return new[] { (byte)(ui >> 24), (byte)(ui >> 16), (byte)(ui >> 8), (byte)ui };
            case ulong ul:
                dataType = 0x0007;
                return new[] { (byte)(ul >> 56), (byte)(ul >> 48), (byte)(ul >> 40), (byte)(ul >> 32), (byte)(ul >> 24), (byte)(ul >> 16), (byte)(ul >> 8), (byte)ul };
            case float f:
                dataType = 0x000B;
                var fb = BitConverter.GetBytes(f);
                return new[] { fb[1], fb[0], fb[3], fb[2] }; // Big-endian
            case double d:
                dataType = 0x000C;
                var db = BitConverter.GetBytes(d);
                return new[] { db[7], db[6], db[5], db[4], db[3], db[2], db[1], db[0] }; // Big-endian
            case string s:
                dataType = 0x000D;
                var sb = System.Text.Encoding.ASCII.GetBytes(s);
                Array.Resize(ref sb, sb.Length + 1); // null terminator
                return sb;
            default:
                dataType = 0x0006; // Default to UDINT
                var val = Convert.ToUInt32(value);
                return new[] { (byte)(val >> 24), (byte)(val >> 16), (byte)(val >> 8), (byte)val };
        }
    }

    public void Dispose()
    {
        Disconnect();
    }
}
