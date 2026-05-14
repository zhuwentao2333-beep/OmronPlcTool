using Opc.Ua;
using System.Net.Sockets;
using System.Text;

namespace OmronPlcTool.Services;

/// <summary>
/// 欧姆龙 NJ/NX 系列 EtherNet/IP 标签通讯。
/// 通过 CIP Read Tag / Write Tag 服务进行变量名读写。
/// 端口: 44818
/// </summary>
public class EtherNetIpService : IPlcCommunication
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private uint _sessionHandle;
    private uint _senderContext;

    public bool IsConnected => _tcp?.Connected == true && _stream != null;
    public event Action<string>? ConnectionStatusChanged;
    public event Action<List<TreeNodeModel>>? TreeBuilt;
    public event Action<string, object?, DateTime>? ValueChanged;

    public async Task ConnectAsync(string ipAddress)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(ipAddress, 44818);
        _stream = _tcp.GetStream();
        _sessionHandle = 0;
        _senderContext = 1;

        // 1. Register CIP Session
        var regReq = new byte[28]; // EIP header (24) + 4 bytes data
        WriteU16(regReq, 0, 0x0065);  // Command: RegisterSession
        WriteU16(regReq, 2, 4);        // Length: 4
        WriteU16(regReq, 24, 1);       // Protocol version: 1
        WriteU16(regReq, 26, 0);       // Option flags: 0

        var regResp = SendReceive(regReq, 28);
        _sessionHandle = ReadU32(regResp, 4);
        ConnectionStatusChanged?.Invoke($"CIP 会话已注册: 0x{_sessionHandle:X8}");

        // 2. Build initial node tree
        await Task.Run(() =>
        {
            var root = new List<TreeNodeModel>
            {
                new() { Name = "全局变量 (输入变量名)", NodeId = "eip:tag", NodeClass = NodeClass.Object },
                new() { Name = "系统变量 (输入变量名)", NodeId = "eip:sys", NodeClass = NodeClass.Object }
            };
            TreeBuilt?.Invoke(root);
        });
    }

    public void Disconnect()
    {
        if (_sessionHandle != 0 && _stream != null)
        {
            try
            {
                var unregReq = new byte[28];
                WriteU16(unregReq, 0, 0x0066);  // UnRegisterSession
                WriteU16(unregReq, 2, 0);
                WriteU32(unregReq, 4, _sessionHandle);
                _stream.Write(unregReq, 0, 28);
                _stream.Flush();
            }
            catch { }
        }
        _stream?.Close();
        _tcp?.Close();
        _stream = null;
        _tcp = null;
        _sessionHandle = 0;
        ConnectionStatusChanged?.Invoke("EtherNet/IP 未连接");
    }

    public List<TreeNodeModel> BrowseChildren(string parentNodeId)
    {
        // EtherNet/IP doesn't have native browsing; return empty
        return new List<TreeNodeModel>();
    }

    public object? ReadValue(string nodeId)
    {
        if (!IsConnected) return null;
        try
        {
            // Parse nodeId format: "eip:VariableName"
            var tagName = nodeId;
            if (tagName.StartsWith("eip:"))
                tagName = tagName[4..];

            return ReadTag(tagName);
        }
        catch (Exception ex)
        {
            ConnectionStatusChanged?.Invoke($"读标签失败: {ex.Message}");
            return null;
        }
    }

    public void WriteValue(string nodeId, object value)
    {
        if (!IsConnected) return;
        try
        {
            var tagName = nodeId;
            if (tagName.StartsWith("eip:"))
                tagName = tagName[4..];

            WriteTag(tagName, value);
        }
        catch (Exception ex)
        {
            ConnectionStatusChanged?.Invoke($"写标签失败: {ex.Message}");
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

    // ---- CIP Tag Read/Write ----

    private object? ReadTag(string tagName)
    {
        var nameBytes = Encoding.ASCII.GetBytes(tagName);
        // Symbolic path: [0x91][len][name]
        int pathBytesLen = 2 + nameBytes.Length;
        int pathWords = (pathBytesLen + 1) / 2;

        // CIP Request: Service(1) + ReqPathSize(1) + Reserved(1) + Path(n) + ReadCount(2)
        int pathPadding = (pathWords * 2) - pathBytesLen;
        int cipLen = 1 + 1 + 1 + pathBytesLen + pathPadding + 2;
        var cipReq = new byte[cipLen];
        int pos = 0;
        cipReq[pos++] = 0x4C;            // Read Tag Service
        cipReq[pos++] = (byte)(pathWords + 0x20);  // Path size in words + padding
        pos++;                            // Reserved (already 0)
        cipReq[pos++] = 0x91;            // Symbolic segment
        cipReq[pos++] = (byte)nameBytes.Length;
        Buffer.BlockCopy(nameBytes, 0, cipReq, pos, nameBytes.Length);
        pos += nameBytes.Length;
        // Padding (zeros already)
        pos += pathPadding;
        WriteU16(cipReq, pos, 1);       // Read 1 element

        var cipResp = SendCipRequest(cipReq);
        if (cipResp == null || cipResp.Length < 4)
            throw new Exception("CIP 响应为空");

        ushort status = cipResp[3];
        if (status != 0)
            throw new Exception($"Read Tag 错误: 0x{status:X2}");

        // Parse response: Service(1) + Reserved(1) + Status(1) + Reserved(1) + DataType(2) + Data...
        int dataPos = 6;
        if (cipResp.Length < dataPos + 1)
            return null;

        ushort dataType = ReadU16(cipResp, dataPos);
        dataPos += 2;

        return ParseCipValue(dataType, cipResp, dataPos);
    }

    private void WriteTag(string tagName, object value)
    {
        var nameBytes = Encoding.ASCII.GetBytes(tagName);
        int pathBytesLen = 2 + nameBytes.Length;
        int pathWords = (pathBytesLen + 1) / 2;
        int pathPadding = (pathWords * 2) - pathBytesLen;

        // Encode value
        (ushort dataType, byte[] data) = EncodeCipValue(value);

        // CIP Request
        int cipLen = 1 + 1 + 1 + pathBytesLen + pathPadding + 2 + 2 + data.Length;
        var cipReq = new byte[cipLen];
        int pos = 0;
        cipReq[pos++] = 0x4D;            // Write Tag Service
        cipReq[pos++] = (byte)(pathWords + 0x20);
        pos++;
        cipReq[pos++] = 0x91;
        cipReq[pos++] = (byte)nameBytes.Length;
        Buffer.BlockCopy(nameBytes, 0, cipReq, pos, nameBytes.Length);
        pos += nameBytes.Length;
        pos += pathPadding;
        WriteU16(cipReq, pos, dataType);  // Data type
        pos += 2;
        WriteU16(cipReq, pos, 1);         // Element count
        pos += 2;
        Buffer.BlockCopy(data, 0, cipReq, pos, data.Length);

        var cipResp = SendCipRequest(cipReq);
        if (cipResp == null || cipResp.Length < 3)
            throw new Exception("写入响应为空");

        ushort status = cipResp[3];
        if (status != 0)
            throw new Exception($"Write Tag 错误: 0x{status:X2}");
    }

    // ---- CIP Encapsulation ----

    private byte[]? SendCipRequest(byte[] cipRequest)
    {
        // EIP Header (24) + SendRRData (4) + Item Header(4) + CIP data
        int eipLen = 24 + 4 + 4 + cipRequest.Length;
        var frame = new byte[eipLen];
        WriteU16(frame, 0, 0x006F);  // SendRRData
        WriteU16(frame, 2, (ushort)(eipLen - 24));
        WriteU32(frame, 4, _sessionHandle);
        WriteU64(frame, 12, _senderContext++);

        int pos = 24;
        WriteU32(frame, pos, 0);      // Interface Handle: 0
        pos += 4;
        WriteU16(frame, pos, 0);      // Timeout: 0
        pos += 2;
        WriteU16(frame, pos, 2);      // Item count: 2
        pos += 2;

        // Address Item (null)
        WriteU32(frame, pos, 0x00000000); pos += 4;

        // Data Item
        WriteU16(frame, pos, 0x00B2);      // Type: Unconnected Data
        pos += 2;
        WriteU16(frame, pos, (ushort)cipRequest.Length);
        pos += 2;
        Buffer.BlockCopy(cipRequest, 0, frame, pos, cipRequest.Length);

        var resp = SendReceive(frame, 24 + 4 + 4 + 256); // 256 bytes max response data
        if (resp.Length < 42)
            throw new Exception("EIP 响应太短");

        // Parse SendRRData response
        int respPos = 24;                    // Skip EIP header
        respPos += 4;                        // Skip Interface Handle
        respPos += 2;                        // Skip Timeout
        ushort itemCount = ReadU16(resp, respPos);
        respPos += 2;

        for (int i = 0; i < itemCount; i++)
        {
            ushort typeId = ReadU16(resp, respPos);
            ushort length = ReadU16(resp, respPos + 2);
            respPos += 4;

            if (typeId == 0x00B2 || typeId == 0x0001) // Unconnected Data or Connected Data
            {
                var data = new byte[length];
                Buffer.BlockCopy(resp, respPos, data, 0, Math.Min(length, resp.Length - respPos));
                return data;
            }
            respPos += length;
        }
        return null;
    }

    private byte[] SendReceive(byte[] sendData, int expectedResponseLen)
    {
        if (_stream == null) throw new InvalidOperationException("未连接");

        _stream.Write(sendData, 0, sendData.Length);
        _stream.Flush();

        var header = new byte[24];
        int read = 0;
        while (read < 24)
        {
            int r = _stream.Read(header, read, 24 - read);
            if (r == 0) throw new SocketException();
            read += r;
        }

        ushort dataLen = ReadU16(header, 2);
        int totalLen = 24 + dataLen;
        var fullResp = new byte[totalLen];
        Buffer.BlockCopy(header, 0, fullResp, 0, 24);

        if (dataLen > 0)
        {
            read = 0;
            while (read < dataLen)
            {
                int r = _stream.Read(fullResp, 24 + read, dataLen - read);
                if (r == 0) throw new SocketException();
                read += r;
            }
        }

        return fullResp;
    }

    // ---- Value Encoding ----

    private static object? ParseCipValue(ushort dataType, byte[] data, int offset)
    {
        return dataType switch
        {
            0xC1 => data[offset] != 0,                // BOOL
            0xC2 => (sbyte)data[offset],              // SINT
            0xC3 => data[offset],                      // USINT (BYTE)
            0xC4 => (short)((data[offset] << 8) | data[offset + 1]),  // INT
            0xC5 => (ushort)((data[offset] << 8) | data[offset + 1]), // UINT
            0xC6 => (int)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]),  // DINT
            0xC7 => (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]), // UDINT
            0xC8 => (float)BitConverter.ToSingle(new[] { data[offset + 1], data[offset], data[offset + 3], data[offset + 2] }, 0), // REAL
            0xC9 => (long)((long)data[offset] << 56 | (long)data[offset + 1] << 48 | (long)data[offset + 2] << 40 | (long)data[offset + 3] << 32 | (long)data[offset + 4] << 24 | (long)data[offset + 5] << 16 | (long)data[offset + 6] << 8 | data[offset + 7]),
            0xCA => (double)BitConverter.ToDouble(new[] { data[offset + 7], data[offset + 6], data[offset + 5], data[offset + 4], data[offset + 3], data[offset + 2], data[offset + 1], data[offset] }, 0),
            0xD0 => Encoding.ASCII.GetString(data, offset + 2, ReadU16(data, offset)), // STRING
            _ => data
        };
    }

    private static (ushort dataType, byte[] data) EncodeCipValue(object value)
    {
        return value switch
        {
            bool b => (0xC1, new byte[] { (byte)(b ? 1 : 0) }),
            sbyte sb => (0xC2, new byte[] { (byte)sb }),
            byte ub => (0xC3, new byte[] { ub }),
            short s => (0xC4, new[] { (byte)(s >> 8), (byte)s }),
            ushort us => (0xC5, new[] { (byte)(us >> 8), (byte)us }),
            int i => (0xC6, new[] { (byte)(i >> 24), (byte)(i >> 16), (byte)(i >> 8), (byte)i }),
            uint ui => (0xC7, new[] { (byte)(ui >> 24), (byte)(ui >> 16), (byte)(ui >> 8), (byte)ui }),
            float f => (0xC8, ToBigEndian(BitConverter.GetBytes(f))),
            long l => (0xC9, ToBigEndian(BitConverter.GetBytes(l))),
            double d => (0xCA, ToBigEndian(BitConverter.GetBytes(d))),
            string s => (0xD0, EncodeString(s)),
            _ => (0xC7, new[] { (byte)(Convert.ToUInt32(value) >> 24), (byte)(Convert.ToUInt32(value) >> 16), (byte)(Convert.ToUInt32(value) >> 8), (byte)Convert.ToUInt32(value) })
        };
    }

    private static byte[] ToBigEndian(byte[] bytes)
    {
        Array.Reverse(bytes);
        return bytes;
    }

    private static byte[] EncodeString(string s)
    {
        var ascii = Encoding.ASCII.GetBytes(s);
        var result = new byte[2 + ascii.Length];
        WriteU16(result, 0, (ushort)ascii.Length);
        Buffer.BlockCopy(ascii, 0, result, 2, ascii.Length);
        return result;
    }

    // ---- Endian Helpers ----

    private static void WriteU16(byte[] buf, int offset, ushort val)
    {
        buf[offset] = (byte)(val & 0xFF);
        buf[offset + 1] = (byte)(val >> 8);
    }

    private static void WriteU32(byte[] buf, int offset, uint val)
    {
        buf[offset] = (byte)(val & 0xFF);
        buf[offset + 1] = (byte)((val >> 8) & 0xFF);
        buf[offset + 2] = (byte)((val >> 16) & 0xFF);
        buf[offset + 3] = (byte)(val >> 24);
    }

    private static void WriteU64(byte[] buf, int offset, ulong val)
    {
        for (int i = 0; i < 8; i++)
            buf[offset + i] = (byte)((val >> (i * 8)) & 0xFF);
    }

    private static ushort ReadU16(byte[] buf, int offset)
        => (ushort)(buf[offset] | (buf[offset + 1] << 8));

    private static uint ReadU32(byte[] buf, int offset)
        => (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));

    public void Dispose()
    {
        Disconnect();
    }
}
