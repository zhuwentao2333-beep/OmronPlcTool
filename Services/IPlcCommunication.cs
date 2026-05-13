namespace OmronPlcTool.Services;

public interface IPlcCommunication : IDisposable
{
    bool IsConnected { get; }
    event Action<string>? ConnectionStatusChanged;
    event Action<List<TreeNodeModel>>? TreeBuilt;
    event Action<string, object?, DateTime>? ValueChanged;

    Task ConnectAsync(string ipAddress);
    void Disconnect();
    List<TreeNodeModel> BrowseChildren(string parentNodeId);
    object? ReadValue(string nodeId);
    void WriteValue(string nodeId, object value);
    void StartMonitoring(IEnumerable<string> nodeIds, int samplingInterval = 500);
}
