namespace OmlTerminal.Core.Models;

/// <summary>A "connect to a whole lab" link from OML Labs, as opposed to a single pre-filled node (OmlNodeLink).
/// The link itself carries no connection details - only a short-lived token used to ask omlHost for the lab's
/// current node list, since nodes' ports are assigned dynamically as VMs start.</summary>
public sealed record OmlLabLink(string OmlHost, string LabId, string LabName, string Token, string Source);

public sealed record OmlLabLinkResult(OmlLabLink? Link, string? Error)
{
    public bool Ok => Link is not null;
}

public enum OmlConsoleType { None, Telnet, Vnc, Rdp, Ssh }

/// <summary>One VM/device in an OML lab, as returned by GET {omlHost}/api/labs/{labId}/nodes/.</summary>
public sealed record OmlLabNode(
    string Id, string Name, string NodeType, string DeviceTemplate, string Status,
    OmlConsoleType ConsoleType, int? ConsolePort, int? VncPort, int? VncWsPort, int? RdpPort)
{
    public bool IsRunning => Status.Equals("running", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether OmlTerminal currently knows how to open a console for this node at all.</summary>
    public bool Connectable => ConsoleType != OmlConsoleType.None;

    /// <summary>Whether a connection could actually be opened right now: running, with the console type's port assigned.</summary>
    public bool IsReadyToConnect => IsRunning && ConsoleType switch
    {
        OmlConsoleType.Rdp => RdpPort is not null,
        OmlConsoleType.Telnet or OmlConsoleType.Ssh => ConsolePort is not null,
        OmlConsoleType.Vnc => VncWsPort is not null,
        _ => false,
    };
}
