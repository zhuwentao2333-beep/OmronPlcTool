using Opc.Ua;
using Opc.Ua.Client;
using System.Collections.Concurrent;
using System.IO;

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

    public bool IsConnected => _session?.Connected == true;

    public event Action<List<TreeNodeModel>>? TreeBuilt;
    public event Action<string, object?, DateTime>? ValueChanged;
    public event Action<string>? ConnectionStatusChanged;

    public async Task ConnectAsync(string ipAddress)
    {
        var url = $"opc.tcp://{ipAddress}:4840";

        // Store under LocalAppData (always writable, even without admin rights)
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OPC Foundation", "CertificateStores");
        var appDir = Path.Combine(baseDir, "MachineDefault");
        foreach (var dir in new[] { appDir,
            Path.Combine(baseDir, "UA Certificate Authorities"),
            Path.Combine(baseDir, "UA Applications"),
            Path.Combine(baseDir, "RejectedCertificates") })
            Directory.CreateDirectory(dir);

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
                    SubjectName = $"CN=OmronPlcTool"
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
            TraceConfiguration = new TraceConfiguration()
        };

        _config.Validate(ApplicationType.Client).GetAwaiter().GetResult();

        // Auto-accept all server certificates (trust on first use)
        _config.CertificateValidator.CertificateValidation += (s, e) =>
        {
            e.Accept = true;
            ConnectionStatusChanged?.Invoke("已接受服务器证书（信任首次连接）");
        };

        await _config.CertificateValidator.Update(_config).ConfigureAwait(false);

        // Let the SDK auto-create application certificate if missing
        var hasCert = await _config.SecurityConfiguration.ApplicationCertificate
            .Find(true).ConfigureAwait(false);
        if (hasCert == null)
        {
            ConnectionStatusChanged?.Invoke("SDK 将自动创建应用证书...");
        }

        // Discover server endpoints
        using var discovery = DiscoveryClient.Create(
            _config,
            new Uri(url),
            EndpointConfiguration.Create(_config));
        discovery.OperationTimeout = 10000;

        var endpoints = await Task.Run(() =>
        {
            try { return discovery.GetEndpoints(null); }
            catch { return null; }
        }).ConfigureAwait(false);

        if (endpoints == null || endpoints.Count == 0)
            throw new Exception($"无法获取 OPC UA 端点。请确认:\n1. PLC IP 是否正确: {ipAddress}\n2. PLC 的 OPC UA 服务器是否已在 Sysmac Studio 中启用\n3. 防火墙是否允许端口 4840");

        ConnectionStatusChanged?.Invoke($"发现 {endpoints.Count} 个端点");

        // Select best available endpoint
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
            if (selected != null) break;
        }
        selected ??= endpoints[0];

        ConnectionStatusChanged?.Invoke($"连接: {selected.SecurityMode}, {selected.SecurityPolicyUri?.Split('/').Last() ?? "None"}");

        var epConfig = EndpointConfiguration.Create(_config);
        var endpoint = new ConfiguredEndpoint(null, selected, epConfig);

        _session = await Session.Create(
            _config,
            endpoint,
            false,
            "OmronPlcTool",
            60000,
            new UserIdentity(new AnonymousIdentityToken()),
            null
        ).ConfigureAwait(false);

        ConnectionStatusChanged?.Invoke($"已连接: {url}");
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
