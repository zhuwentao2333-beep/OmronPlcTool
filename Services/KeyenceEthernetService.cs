using Opc.Ua;
using System.Net.Sockets;
using System.Text;

namespace OmronPlcTool.Services;

/// <summary>
/// 基恩士 KV-8000/KV-X 系列以太网标签通讯。
/// 使用上位链路 (Upper Link) 协议通过变量名进行读写。
/// 端口: 8501
/// </summary>
public class KeyenceEthernetService : IPlcCommunication
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _serialNumber;

    public bool IsConnected => _tcp?.Connected == true && _stream != null;
    public event Action<string>? ConnectionStatusChanged;
    public event Action<List<TreeNodeModel>>? TreeBuilt;
    public event Action<string, object?, DateTime>? ValueChanged;

    public async Task ConnectAsync(string ipAddress)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(ipAddress, 8501);
        _stream = _tcp.GetStream();
        _serialNumber = 1;
        ConnectionStatusChanged?.Invoke($"KV Ethernet 已连接: {ipAddress}:8501");

        await Task.Run(() =>
        {
            var root = new List<TreeNodeModel>
            {
                new() { Name = "全局变量 (输入变量名)", NodeId = "kv:tag", NodeClass = NodeClass.Object },
                new() { Name = "系统变量 (输入变量名)", NodeId = "kv:sys", NodeClass = NodeClass.Object }
            };
            TreeBuilt?.Invoke(root);
        });
    }

    public void Disconnect()
    {
        _stream?.Close();
        _tcp?.Close();
        _stream = null;
        _tcp = null;
        ConnectionStatusChanged?.Invoke("KV Ethernet 未连接");
    }

    public List<TreeNodeModel> BrowseChildren(string parentNodeId)
        => new List<TreeNodeModel>();

    public object? ReadValue(string nodeId)
    {
        if (!IsConnected) return null;
        try
        {
            var tagName = nodeId.StartsWith("kv:") ? nodeId[3..] : nodeId;
            return ReadVariable(tagName);
        }
        catch (Exception ex)
        {
            ConnectionStatusChanged?.Invoke($"读变量失败: {ex.Message}");
            return null;
        }
    }

    public void WriteValue(string nodeId, object value)
    {
        if (!IsConnected) return;
        try
        {
            var tagName = nodeId.StartsWith("kv:") ? nodeId[3..] : nodeId;
            WriteVariable(tagName, value);
        }
        catch (Exception ex)
        {
            ConnectionStatusChanged?.Invoke($"写变量失败: {ex.Message}");
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

    // ---- Protocol Implementation ----

    /// <summary>
    /// KV Ethernet 命令帧:
    ///   [2 bytes: serial number (big-endian)]
    ///   [2 bytes: reserved (0x0000)]
    ///   [2 bytes: command code]
    ///   [2 bytes: data length (big-endian)]
    ///   [N bytes: data]
    /// </summary>
    private byte[] BuildFrame(ushort command, byte[] data)
    {
        ushort sn = _serialNumber++;
        var frame = new byte[8 + data.Length];
        frame[0] = (byte)(sn >> 8);   // Serial MSB
        frame[1] = (byte)sn;          // Serial LSB
        // reserved: already 0
        frame[4] = (byte)(command >> 8);
        frame[5] = (byte)command;
        frame[6] = (byte)(data.Length >> 8);
        frame[7] = (byte)data.Length;
        Buffer.BlockCopy(data, 0, frame, 8, data.Length);
        return frame;
    }

    private byte[] SendFrame(byte[] frame, int expectedMinLen = 8)
    {
        if (_stream == null) throw new InvalidOperationException("未连接");

        _stream.Write(frame, 0, frame.Length);
        _stream.Flush();

        // Read response header (8 bytes)
        var header = new byte[8];
        int read = 0;
        while (read < 8)
        {
            int r = _stream.Read(header, read, 8 - read);
            if (r == 0) throw new SocketException();
            read += r;
        }

        ushort dataLen = (ushort)((header[6] << 8) | header[7]);
        var respData = new byte[dataLen];
        if (dataLen > 0)
        {
            read = 0;
            while (read < dataLen)
            {
                int r = _stream.Read(respData, read, dataLen - read);
                if (r == 0) throw new SocketException();
                read += r;
            }
        }

        // Check response command (bit 15 set means response)
        ushort respCmd = (ushort)((header[4] << 8) | header[5]);
        if ((respCmd & 0x8000) == 0 && respCmd != 0)
        {
            // Error code in response
            ushort errorCode = (ushort)((respData.Length >= 2) ? (respData[0] << 8 | respData[1]) : respCmd);
            throw new Exception($"PLC 返回错误: 0x{errorCode:X4}");
        }

        return respData;
    }

    // ---- Variable Read/Write ----

    /// <summary>
    /// 读变量命令: 0x0401
    /// 数据: [2 bytes: name length] [N bytes: name ASCII]
    /// 响应: [1 byte: data type] [N bytes: value]
    /// </summary>
    private object? ReadVariable(string name)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var data = new byte[2 + nameBytes.Length];
        data[0] = (byte)(nameBytes.Length >> 8);
        data[1] = (byte)nameBytes.Length;
        Buffer.BlockCopy(nameBytes, 0, data, 2, nameBytes.Length);

        var frame = BuildFrame(0x0401, data);
        var resp = SendFrame(frame, 8);

        if (resp.Length < 1)
            return null;

        return ParseVariableValue(resp[0], resp, 1);
    }

    /// <summary>
    /// 写变量命令: 0x0402
    /// 数据: [2:name len] [N:name] [1:type] [N:value]
    /// </summary>
    private void WriteVariable(string name, object value)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        (byte typeCode, byte[] valData) = EncodeValue(value);

        var data = new byte[2 + nameBytes.Length + 1 + valData.Length];
        data[0] = (byte)(nameBytes.Length >> 8);
        data[1] = (byte)nameBytes.Length;
        Buffer.BlockCopy(nameBytes, 0, data, 2, nameBytes.Length);
        int pos = 2 + nameBytes.Length;
        data[pos] = typeCode;
        pos++;
        Buffer.BlockCopy(valData, 0, data, pos, valData.Length);

        var frame = BuildFrame(0x0402, data);
        var resp = SendFrame(frame, 8);

        // Check response code
        if (resp.Length >= 2)
        {
            ushort result = (ushort)((resp[0] << 8) | resp[1]);
            if (result != 0)
                throw new Exception($"写入失败: 0x{result:X4}");
        }
    }

    // ---- Data Type Encoding ----

    // Type codes for KV series
    private static object? ParseVariableValue(byte typeCode, byte[] data, int offset)
    {
        return typeCode switch
        {
            0x00 => null,                          // Empty
            0x01 => data[offset] != 0,            // Bit / BOOL
            0x02 => data[offset],                  // Byte / USINT
            0x03 => (sbyte)data[offset],          // SINT
            0x04 => (short)((data[offset] << 8) | data[offset + 1]),  // INT
            0x05 => (ushort)((data[offset] << 8) | data[offset + 1]), // WORD / UINT
            0x06 => (int)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]), // DINT
            0x07 => (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]), // UDINT
            0x08 => (long)((long)data[offset] << 56 | (long)data[offset + 1] << 48 | (long)data[offset + 2] << 40 | (long)data[offset + 3] << 32 | (long)data[offset + 4] << 24 | (long)data[offset + 5] << 16 | (long)data[offset + 6] << 8 | data[offset + 7]),
            0x09 => (float)BitConverter.ToSingle(new[] { data[offset + 1], data[offset], data[offset + 3], data[offset + 2] }, 0),
            0x0A => (double)BitConverter.ToDouble(new[] { data[offset + 7], data[offset + 6], data[offset + 5], data[offset + 4], data[offset + 3], data[offset + 2], data[offset + 1], data[offset] }, 0),
            0x0B => Encoding.ASCII.GetString(data, offset + 2, (data[offset] << 8) | data[offset + 1]), // STRING
            _ => data
        };
    }

    private static (byte typeCode, byte[] data) EncodeValue(object value)
    {
        return value switch
        {
            bool b => (0x01, new byte[] { (byte)(b ? 1 : 0) }),
            byte b => (0x02, new byte[] { b }),
            sbyte sb => (0x03, new byte[] { (byte)sb }),
            short s => (0x04, new[] { (byte)(s >> 8), (byte)s }),
            ushort us => (0x05, new[] { (byte)(us >> 8), (byte)us }),
            int i => (0x06, new[] { (byte)(i >> 24), (byte)(i >> 16), (byte)(i >> 8), (byte)i }),
            uint ui => (0x07, new[] { (byte)(ui >> 24), (byte)(ui >> 16), (byte)(ui >> 8), (byte)ui }),
            long l => (0x08, ToBigEndian(BitConverter.GetBytes(l))),
            float f => (0x09, ToBigEndian(BitConverter.GetBytes(f))),
            double d => (0x0A, ToBigEndian(BitConverter.GetBytes(d))),
            string s => (0x0B, EncodeString(s)),
            _ => (0x06, new[] { (byte)(Convert.ToInt32(value) >> 24), (byte)(Convert.ToInt32(value) >> 16), (byte)(Convert.ToInt32(value) >> 8), (byte)Convert.ToInt32(value) })
        };
    }

    private static byte[] ToBigEndian(byte[] bytes) { Array.Reverse(bytes); return bytes; }

    private static byte[] EncodeString(string s)
    {
        var ascii = Encoding.ASCII.GetBytes(s);
        var result = new byte[2 + ascii.Length];
        result[0] = (byte)(ascii.Length >> 8);
        result[1] = (byte)ascii.Length;
        Buffer.BlockCopy(ascii, 0, result, 2, ascii.Length);
        return result;
    }

    public void Dispose() { Disconnect(); }
}
