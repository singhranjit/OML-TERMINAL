namespace OmlTerminal.Core.Transports;

public enum TunnelKind { Local, Remote, Dynamic }

/// <summary>A user-configured port forward riding on an already-connected SSH session (the MobaXterm "Tunneling" feature).</summary>
public sealed class TunnelSpec
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public TunnelKind Kind { get; set; } = TunnelKind.Local;
    public string BoundHost { get; set; } = "127.0.0.1";

    /// <summary>0 means "pick an available port automatically"; only allowed for Local and Dynamic.</summary>
    public int BoundPort { get; set; }
    public string TargetHost { get; set; } = "";
    public int TargetPort { get; set; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(BoundHost)) errors.Add("Local bind address is required.");
        if (BoundPort == 0 && Kind == TunnelKind.Remote) errors.Add("Remote port must be specified explicitly.");
        if (BoundPort is < 0 or > 65535) errors.Add("Local port must be between 0 and 65535.");
        if (Kind != TunnelKind.Dynamic)
        {
            if (string.IsNullOrWhiteSpace(TargetHost)) errors.Add("Target host is required.");
            if (TargetPort is < 1 or > 65535) errors.Add("Target port must be between 1 and 65535.");
        }
        return errors;
    }

    public string Display => Kind switch
    {
        TunnelKind.Local => $"Local    {BoundHost}:{BoundPort}  ->  {TargetHost}:{TargetPort}",
        TunnelKind.Remote => $"Remote   {BoundHost}:{BoundPort}  <-  {TargetHost}:{TargetPort}",
        _ => $"Dynamic (SOCKS)  {BoundHost}:{BoundPort}",
    };
}

/// <summary>Implemented by transports that can host additional port forwards on top of their connection (built-in SSH only).</summary>
public interface ISshTunnelHost
{
    IReadOnlyList<TunnelSpec> ActiveTunnels { get; }

    /// <summary>Starts the tunnel and returns the spec with BoundPort filled in if it was requested as 0 (automatic).</summary>
    TunnelSpec StartTunnel(TunnelSpec spec);
    void StopTunnel(Guid id);

    /// <summary>Raised (any thread) if a running tunnel fails after having started.</summary>
    event Action<TunnelSpec, string>? TunnelFailed;
}
