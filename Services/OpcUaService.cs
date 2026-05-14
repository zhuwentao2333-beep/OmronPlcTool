using Opc.Ua;
using Opc.Ua.Client;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace OmronPlcTool.Services;

public class TreeNodeModel
{
    public string Name { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public NodeClass NodeClass { get; set; }
    public List<TreeNodeModel> Children { get; set; } = new();
    public bool IsVariable => NodeClass == NodeClass.Variable;
}

public class OpcUaService : IPlcCommunication
{
    private Session? _session;
    private ApplicationConfiguration? _config;
    private static string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "OpcUaDebug.log");

    public bool IsConnected => _session?.Connected == true;

    public event Action<List<TreeNodeModel>>? TreeBuilt;
    public event Action<string, object?, DateTime>? ValueChanged;
    public event Action<string>? ConnectionStatusChanged;

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        try { File.AppendAllText(_logPath, line + Environment.NewLine); } catch { }
        Debug.WriteLine(line);
    }

    public async Task ConnectAsync(string ipAddress)
    {
        Log("===== OPC UA 连接开始 =====");
        Log($"目标 IP: {ipAddress}");
        var url = $"opc.tcp://{ipAddress}:4840";

        // --- Step 0: Raw TCP connectivity test ---
        Log("步骤 0: 测试原始 TCP 连接...");
        ConnectionStatusChanged?.Invoke("测试 TCP 连接...");
        try
        {
            using var tcp = new TcpClient();
            tcp.SendTimeout = 5000;
            tcp.ReceiveTimeout = 5000;
            await tcp.ConnectAsync(ipAddress, 4840).WaitAsync(TimeSpan.FromSeconds(5));
            Log($"TCP 连接成功: {ipAddress}:4840 - {tcp.Connected}");
            tcp.Close();
        }
        catch (Exception ex)
        {
            Log($"TCP 连接失败: {ex.GetType().Name} - {ex.Message}");
            throw new Exception(
                $"无法连接到 {ipAddress}:4840\n\n" +
                $"错误类型: {ex.GetType().Name}\n" +
                $"错误信息: {ex.Message}\n\n" +
                $"请检查网络连接和防火墙设置。\n" +
                $"详细日志: {_logPath}");
        }

        // --- Step 1: Configuration ---
        Log("步骤 1: 初始化 OPC UA 配置...");
        ConnectionStatusChanged?.Invoke("初始化配置...");
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OPC Foundation", "CertificateStores");
        var appDir = Path.Combine(baseDir, "MachineDefault");
        foreach (var dir in new[] { appDir,
            Path.Combine(baseDir, "UA Certificate Authorities"),
            Path.Combine(baseDir, "UA Applications"),
            Path.Combine(baseDir, "RejectedCertificates") })
            Directory.CreateDirectory(dir);
        Log($"证书目录: {appDir}");

        _config = new ApplicationConfiguration
        {
            ApplicationName = "OmronPlcTool",
            ApplicationUri = $"urn:{Environment.MachineName}:OmronPlcTool",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = appDir,
                    SubjectName = "CN=OmronPlcTool"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(baseDir, "UA Certificate Authorities")
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(baseDir, "UA Applications")
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(baseDir, "RejectedCertificates")
                },
                AutoAcceptUntrustedCertificates = true
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
            TraceConfiguration = new Opc.Ua.TraceConfiguration()
        };

        try
        {
            _config.Validate(ApplicationType.Client).GetAwaiter().GetResult();
            Log("配置验证通过");
        }
        catch (Exception ex)
        {
            Log($"配置验证失败: {ex}");
            throw;
        }

        _config.CertificateValidator.CertificateValidation += (s, e) =>
        {
            Log($"证书验证事件: Accept={e.Accept}, StatusCode={e.Error.StatusCode}");
            e.Accept = true;
        };

        try
        {
            await _config.CertificateValidator.Update(_config).ConfigureAwait(false);
            Log("证书验证器更新完成");
        }
        catch (Exception ex)
        {
            Log($"证书验证器更新失败: {ex}");
            ConnectionStatusChanged?.Invoke($"证书初始化警告: {ex.Message}");
        }

        // --- Step 2: Application certificate ---
        Log("步骤 2: 检查应用证书...");
        try
        {
            var hasCert = await _config.SecurityConfiguration.ApplicationCertificate
                .Find(true).ConfigureAwait(false);
            Log(hasCert != null ? $"应用证书已存在: {hasCert.Subject}" : "应用证书不存在（SDK 将自动创建）");
        }
        catch (Exception ex)
        {
            Log($"证书检查异常: {ex}");
        }

        // --- Step 3: Discover endpoints ---
        Log($"步骤 3: 发现端点 {url}...");
        ConnectionStatusChanged?.Invoke("正在发现 OPC UA 端点...");

        EndpointDescriptionCollection? endpoints = null;
        Exception? discoveryError = null;

        try
        {
            using var discovery = DiscoveryClient.Create(
                _config,
                new Uri(url),
                EndpointConfiguration.Create(_config));
            discovery.OperationTimeout = 15000;
            Log($"DiscoveryClient 创建成功, 超时={discovery.OperationTimeout}ms");

            endpoints = await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var eps = discovery.GetEndpoints(null);
                    sw.Stop();
                    Log($"GetEndpoints 完成: 耗时={sw.ElapsedMilliseconds}ms, 端点数={eps?.Count ?? 0}");
                    if (eps != null)
                    {
                        foreach (var ep in eps)
                            Log($"  端点: {ep.EndpointUrl} Security={ep.SecurityMode} Policy={ep.SecurityPolicyUri?.Split('/').Last() ?? "None"} Level={ep.SecurityLevel}");
                    }
                    return eps;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    Log($"GetEndpoints 异常: {ex.GetType().Name} - {ex.Message} (耗时={sw.ElapsedMilliseconds}ms)");
                    if (ex.InnerException != null)
                        Log($"  内部异常: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}");
                    discoveryError = ex;
                    return null;
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"DiscoveryClient 创建失败: {ex.GetType().Name} - {ex.Message}");
            discoveryError = ex;
        }

        if (endpoints == null || endpoints.Count == 0)
        {
            var errMsg = discoveryError != null
                ? $"{discoveryError.GetType().Name}: {discoveryError.Message}"
                : "服务器未返回任何端点";
            Log($"端点发现失败: {errMsg}");
            Log($"详细日志已保存到: {_logPath}");
            throw new Exception(
                $"无法获取 OPC UA 端点 ({url})\n\n" +
                $"错误: {errMsg}\n\n" +
                $"请确认:\n" +
                $"  1. PLC IP: {ipAddress}（FINS 已通不代表 OPC UA 端口通）\n" +
                $"  2. Sysmac Studio → 内置EtherNet/IP端口设置 → OPC UA 服务器 → 启用\n" +
                $"  3. OPC UA 端口号确认为 4840\n" +
                $"  4. 防火墙是否拦截了 4840 端口\n\n" +
                $"完整日志: {_logPath}");
        }

        ConnectionStatusChanged?.Invoke($"发现 {endpoints.Count} 个端点，选择最佳...");
        Log($"共发现 {endpoints.Count} 个端点");

        // --- Step 4: Select endpoint ---
        EndpointDescription? selected = null;
        foreach (var mode in new[] {
            MessageSecurityMode.None,
            MessageSecurityMode.Sign,
            MessageSecurityMode.SignAndEncrypt })
        {
            selected = endpoints
                .Where(e => e.SecurityMode == mode)
                .OrderByDescending(e => e.SecurityLevel)
                .FirstOrDefault();
            if (selected != null)
            {
                Log($"选择端点: {selected.EndpointUrl} Mode={mode} Policy={selected.SecurityPolicyUri?.Split('/').Last()}");
                break;
            }
        }
        selected ??= endpoints[0];
        Log($"最终端点: {selected.EndpointUrl}");

        ConnectionStatusChanged?.Invoke($"使用: {selected.SecurityMode}, {selected.SecurityPolicyUri?.Split('/').Last() ?? "None"}");

        // --- Step 5: Create session ---
        Log("步骤 5: 创建会话...");
        ConnectionStatusChanged?.Invoke("创建会话...");
        try
        {
            var epConfig = EndpointConfiguration.Create(_config);
            var endpoint = new ConfiguredEndpoint(null, selected, epConfig);
            Log($"ConfiguredEndpoint 创建: {endpoint.EndpointUrl}");

            var sw = Stopwatch.StartNew();
            _session = await Session.Create(
                _config,
                endpoint,
                false,
                "OmronPlcTool",
                60000,
                new UserIdentity(new AnonymousIdentityToken()),
                null
            ).ConfigureAwait(false);
            sw.Stop();
            Log($"会话创建成功: 耗时={sw.ElapsedMilliseconds}ms, SessionId={_session.SessionId}");
        }
        catch (Exception ex)
        {
            Log($"会话创建失败: {ex.GetType().Name} - {ex.Message}");
            if (ex.InnerException != null)
                Log($"  内部异常: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}");
            throw new Exception(
                $"OPC UA 会话创建失败\n\n" +
                $"错误: {ex.GetType().Name}: {ex.Message}\n\n" +
                $"端点: {selected.EndpointUrl}\n" +
                $"安全模式: {selected.SecurityMode}\n" +
                $"完整日志: {_logPath}");
        }

        ConnectionStatusChanged?.Invoke($"已连接: {url}");
        Log("===== OPC UA 连接成功 =====");

        // --- Step 6: Browse node tree ---
        Log("步骤 6: 浏览节点树...");
        await Task.Run(() => BuildNodeTree());
    }

    public void Disconnect()
    {
        _session?.Close();
        _session?.Dispose();
        _session = null;
        ConnectionStatusChanged?.Invoke("未连接");
    }

    private void BuildNodeTree()
    {
        if (_session == null) return;

        var rootNodes = new List<TreeNodeModel>();
        try
        {
            BrowseChildren(ObjectIds.ObjectsFolder.ToString(), rootNodes);
        }
        catch (Exception ex)
        {
            ConnectionStatusChanged?.Invoke($"浏览节点失败: {ex.Message}");
            return;
        }

        TreeBuilt?.Invoke(rootNodes);
    }

    public List<TreeNodeModel> BrowseChildren(string parentNodeId)
    {
        if (_session == null) return new List<TreeNodeModel>();

        var children = new List<TreeNodeModel>();
        BrowseChildren(parentNodeId, children);
        return children;
    }

    private void BrowseChildren(string parentNodeId, List<TreeNodeModel> targetList)
    {
        if (_session == null) return;

        try
        {
            var browseDesc = new BrowseDescription
            {
                NodeId = NodeId.Parse(parentNodeId),
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable),
                ResultMask = (uint)BrowseResultMask.All
            };

            var browseCollection = new BrowseDescriptionCollection { browseDesc };
            _session.Browse(
                null,
                null,
                0,
                browseCollection,
                out var results,
                out var _
            );

            if (results == null || results.Count == 0) return;

            foreach (var reference in results[0].References)
            {
                var child = new TreeNodeModel
                {
                    Name = reference.DisplayName?.Text ?? reference.ToString(),
                    NodeId = reference.NodeId.ToString(),
                    NodeClass = reference.NodeClass
                };
                targetList.Add(child);
            }
        }
        catch
        {
            // Node might not have children or browsing not allowed
        }
    }

    public object? ReadValue(string nodeId)
    {
        if (_session == null) return null;

        try
        {
            var node = NodeId.Parse(nodeId);
            var value = _session.ReadValue(node);
            return UnwrapValue(value);
        }
        catch
        {
            return null;
        }
    }

    public void WriteValue(string nodeId, object value)
    {
        if (_session == null) return;

        try
        {
            var node = NodeId.Parse(nodeId);
            var type = _session.ReadValue(node).WrappedValue.TypeInfo.BuiltInType;

            var variant = ConvertToVariant(value, type);
            var writeValue = new WriteValue
            {
                NodeId = node,
                AttributeId = Attributes.Value,
                Value = new DataValue(variant)
            };

            var writeCollection = new WriteValueCollection { writeValue };
            _session.Write(null, writeCollection, out var results, out var _);

            if (results[0] != StatusCodes.Good)
            {
                ConnectionStatusChanged?.Invoke($"写入失败: {results[0]}");
            }
        }
        catch (Exception ex)
        {
            ConnectionStatusChanged?.Invoke($"写入错误: {ex.Message}");
        }
    }

    public void StartMonitoring(IEnumerable<string> nodeIds, int samplingInterval = 500)
    {
        if (_session == null) return;

        var subscription = new Subscription
        {
            PublishingInterval = samplingInterval
        };
        _session.AddSubscription(subscription);

        foreach (var nodeId in nodeIds)
        {
            var item = new MonitoredItem(subscription.DefaultItem)
            {
                DisplayName = nodeId,
                StartNodeId = NodeId.Parse(nodeId),
                AttributeId = Attributes.Value,
                MonitoringMode = MonitoringMode.Reporting,
                SamplingInterval = samplingInterval,
                QueueSize = 2,
                DiscardOldest = true
            };

            item.Notification += (monitoredItem, args) =>
            {
                foreach (var value in monitoredItem.DequeueValues())
                {
                    var displayName = monitoredItem.DisplayName;
                    var unwrapped = UnwrapValue(value);
                    ValueChanged?.Invoke(displayName, unwrapped, value.SourceTimestamp);
                }
            };

            subscription.AddItem(item);
        }

        subscription.Create();
    }

    private static object? UnwrapValue(DataValue? dataValue)
    {
        if (dataValue == null || dataValue.WrappedValue == Variant.Null)
            return null;

        return dataValue.WrappedValue.Value;
    }

    private static Variant ConvertToVariant(object value, BuiltInType targetType)
    {
        return targetType switch
        {
            BuiltInType.Boolean => new Variant(Convert.ToBoolean(value)),
            BuiltInType.SByte => new Variant(Convert.ToSByte(value)),
            BuiltInType.Byte => new Variant(Convert.ToByte(value)),
            BuiltInType.Int16 => new Variant(Convert.ToInt16(value)),
            BuiltInType.UInt16 => new Variant(Convert.ToUInt16(value)),
            BuiltInType.Int32 => new Variant(Convert.ToInt32(value)),
            BuiltInType.UInt32 => new Variant(Convert.ToUInt32(value)),
            BuiltInType.Int64 => new Variant(Convert.ToInt64(value)),
            BuiltInType.UInt64 => new Variant(Convert.ToUInt64(value)),
            BuiltInType.Float => new Variant(Convert.ToSingle(value)),
            BuiltInType.Double => new Variant(Convert.ToDouble(value)),
            BuiltInType.String => new Variant(Convert.ToString(value) ?? string.Empty),
            _ => new Variant(value)
        };
    }

    public void Dispose()
    {
        _session?.Close();
        _session?.Dispose();
        _session = null;
    }
}
