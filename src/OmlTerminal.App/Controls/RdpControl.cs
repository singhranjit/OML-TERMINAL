using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.UI.Xaml;
using Grid = Microsoft.UI.Xaml.Controls.Grid;
using UserControl = Microsoft.UI.Xaml.Controls.UserControl;

namespace OmlTerminal.App.Controls;

/// <summary>
/// Embeds a real RDP session inside a tab, the same way tools like MobaXterm/Remote Desktop Manager do, instead
/// of shelling out to mstsc.exe in its own window. There is no supported WinUI 3/managed RDP client, so this hosts
/// the same ActiveX control mstsc.exe itself is built on (mstscax.dll's "MsRdpClient11NotSafeForScripting"), which
/// gets NLA/CredSSP for free since it's Microsoft's own client code.
///
/// Getting a legacy ActiveX control working here needs three things dotnet build can't give us out of the box:
///  1. tlbimp/aximp-generated interop - unavailable ('ResolveComReference' needs full .NET Framework MSBuild, not
///     the .NET SDK's). Instead this hosts the control directly via System.Windows.Forms.AxHost's CLSID-string
///     constructor and talks to it through `dynamic` (late-bound IDispatch) - no generated assembly needed.
///  2. Events - AxHost normally gets these from an aximp-generated sink. Built by hand here: IMsTscAxEvents below
///     is a hand-written dispinterface (name/IID/DISPIDs read directly out of mstscax.dll's real type library, not
///     guessed) wired up through AxHost's own CreateSink()/ConnectionPointCookie, the same mechanism aximp uses.
///  3. A host window - WinUI 3 renders its whole window through DirectComposition as one flattened, GPU-composited
///     surface. A legacy HWND simply reparented as a WS_CHILD sibling of that surface (the "classic airspace"
///     trick that used to work before native composition) does NOT reliably interleave with it: DirectComposition
///     recomposites over plain child HWNDs on essentially any redraw, regardless of Win32 z-order - confirmed by
///     testing (continuous HWND_TOP/HWND_TOPMOST reassertion made no difference). Real WPF/Win32 tools like
///     MobaXterm don't hit this because they have an actual HwndHost mechanism; WinUI 3 doesn't have an equivalent.
///     Instead, this hosts the ActiveX control inside its own separate, borderless, taskbar-hidden top-level
///     window and keeps it positioned exactly over the XAML placeholder on every layout pass, shown/hidden with
///     the tab's Loaded/Unloaded lifecycle, and z-ordered directly above the main window on every sync. This is a
///     genuinely independent top-level window composited by DWM the normal way, which sidesteps the DirectComposition
///     airspace problem entirely instead of fighting it.
///
///     This window is NOT made an owned window of the main one (no GWLP_HWNDPARENT). That was tried first and
///     reproducibly crashed the whole app with a native access violation inside Microsoft.UI.Xaml.dll itself (not
///     this code, not mstscax.dll) - consistently the same faulting module and offset, on the very next connect
///     after the app launched. WinUI 3's own window-management internals appear to choke on an owner relationship
///     they don't recognize on their main HWND. Explicit z-order reassertion (see SyncBounds) replaces the
///     automatic above-owner stacking that ownership would otherwise have provided for free.
/// </summary>
public sealed class RdpControl : UserControl
{
    private const string ClsidMsRdpClient11 = "{1DF7C823-B2D4-4B54-975A-F2AC5D7CF8B8}";

    private readonly Grid _placeholder = new();
    private RdpAxHost? _ax;
    private System.Windows.Forms.Form? _rdpWindow;
    private System.Drawing.Rectangle _lastBounds = System.Drawing.Rectangle.Empty;
    private bool _closedRaised;
    private bool _detached;
    private bool _connected;
    private CancellationTokenSource? _resizeDebounceCts;
    private (string Host, int Port, string Username, string Password, string Domain)? _pendingConnect;

    /// <summary>Raised once the session is fully up (post-login).</summary>
    public event Action? Connected;

    /// <summary>Raised once, whenever the session ends - argument is a human-readable reason, or null for a clean disconnect.</summary>
    public event Action<string?>? Closed;

    public RdpControl()
    {
        Content = _placeholder;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LayoutUpdated += (_, _) => SyncBounds();
    }

    /// <summary>Queues a connect and, if the tab is already fully laid out, starts it now; otherwise it starts as
    /// soon as OnLoaded shows and positions the host window. Calling ocx.Connect() before the window has actually
    /// been shown at its real size (which - since MainWindow calls this synchronously right after creating the
    /// tab, before XAML has run a layout pass - could otherwise happen on every connect) reproduced an intermittent
    /// "connects and logs in cleanly but never paints a frame" failure in testing; the ActiveX control appears to
    /// need to already be sited in a real, visible, correctly-sized window before Connect() negotiates the session.</summary>
    public void Connect(string host, int port, string username, string password, string domain = "")
    {
        EnsureCreated();
        _pendingConnect = (host, port, username, password, domain);
        // Always queued, never called inline here: this method itself typically runs from inside a WinUI event
        // handler (a tab double-click, Enter key, etc.), which is exactly the kind of nested call stack that made
        // ocx.Connect()'s internal message pumping crash the process when called synchronously (see OnLoaded).
        if (_rdpWindow!.Visible) DispatcherQueue.TryEnqueue(StartPendingConnect);
    }

    private void StartPendingConnect()
    {
        if (_detached || _pendingConnect is not { } c) return;
        _pendingConnect = null;

        dynamic ocx = _ax!.OcxObject;
        ocx.Server = c.Host;
        if (!string.IsNullOrEmpty(c.Domain)) ocx.Domain = c.Domain;
        if (!string.IsNullOrEmpty(c.Username)) ocx.UserName = c.Username;

        // Negotiate the session at the tab's actual current size (SyncBounds has already run at least once by
        // this point - see OnLoaded) instead of the arbitrary 800x600 the host window starts at, so the desktop
        // doesn't open at the wrong resolution and then have to catch up via a resize once connected.
        if (_lastBounds.Width > 0 && _lastBounds.Height > 0)
        {
            ocx.DesktopWidth = _lastBounds.Width;
            ocx.DesktopHeight = _lastBounds.Height;
        }

        dynamic adv = ocx.AdvancedSettings;
        if (c.Port is not (3389 or 0)) adv.RDPPort = c.Port;
        if (!string.IsNullOrEmpty(c.Password)) adv.ClearTextPassword = c.Password;
        adv.EnableCredSspSupport = true;      // NLA - required by default on modern Windows Server/11 targets
        adv.AuthenticationLevel = 0;          // connect even if the server's certificate can't be verified (self-signed lab hosts)
        adv.SmartSizing = true;               // smooths over the gap between a resize and the server catching up to it (see NotifyServerOfResize)
        adv.EnableWindowsKey = 1;
        ocx.Connect();
    }

    public void Disconnect()
    {
        try { (_ax?.OcxObject as dynamic)?.Disconnect(); } catch { }
    }

    public void Detach()
    {
        if (_detached) return;
        _detached = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _pendingConnect = null;
        _resizeDebounceCts?.Cancel();

        // _detached is checked inside every queued event callback below, so a COM callback already in flight
        // on the way to the UI thread can't run its body against a control that's mid-dispose.
        Disconnect();
        _ax?.Dispose();
        _ax = null;
        _rdpWindow?.Dispose();
        _rdpWindow = null;
    }

    private void EnsureCreated()
    {
        if (_ax is not null) return;

        _rdpWindow = new System.Windows.Forms.Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Size = new System.Drawing.Size(800, 600),
        };
        _ = _rdpWindow.Handle; // force handle creation so it can be positioned before Show()

        _ax = new RdpAxHost { Dock = DockStyle.Fill };
        _rdpWindow.Controls.Add(_ax);
        _ax.CreateControl();

        _ax.RdpConnected += () => DispatcherQueue.TryEnqueue(() => { if (!_detached) { _connected = true; Connected?.Invoke(); } });
        _ax.RdpDisconnected += reason => DispatcherQueue.TryEnqueue(() => { if (!_detached) { _connected = false; RaiseClosed(DisconnectReasonText(reason)); } });
        _ax.RdpFatalError += code => DispatcherQueue.TryEnqueue(() => { if (!_detached) { _connected = false; RaiseClosed($"RDP error {code}"); } });

        SyncBounds(force: true);
    }

    private void RaiseClosed(string? reason)
    {
        if (_closedRaised) return;
        _closedRaised = true;
        Closed?.Invoke(reason);
    }

    private static string? DisconnectReasonText(int reason) => reason switch
    {
        0 or 1 => null, // app-initiated / no error
        _ => $"Disconnected (reason {reason})",
    };

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_detached) return;
        EnsureCreated();
        SyncBounds(force: true);
        _rdpWindow!.Show();
        // Not called synchronously here: Loaded fires from inside WinUI 3's own internal event-dispatch call
        // stack, and ocx.Connect() does real synchronous COM/OLE work that can pump the Windows message queue -
        // doing that re-entrantly, nested inside WinUI 3's own dispatch, reproducibly crashed the whole process
        // with a native access violation inside Microsoft.UI.Xaml.dll itself (confirmed via crash dump analysis:
        // the faulting thread's managed stack ran OnLoaded -> StartPendingConnect -> ocx.Connect() directly under
        // Microsoft.UI.Xaml.Application.Start). Queuing it lets Loaded's dispatch fully unwind back to the main
        // message loop first, so Connect()'s internal message pumping is no longer nested inside anything.
        DispatcherQueue.TryEnqueue(StartPendingConnect);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _rdpWindow?.Hide();
    }

    private void SyncBounds(bool force = false)
    {
        if (_detached || _rdpWindow is null || !force && !HasValidLayout) return;
        if (XamlRoot is null) return;

        var mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        var origin = new NativeMethods.POINT();
        NativeMethods.ClientToScreen(mainHwnd, ref origin);

        var transform = _placeholder.TransformToVisual(null);
        var topLeft = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        double scale = XamlRoot.RasterizationScale;

        var bounds = new System.Drawing.Rectangle(
            origin.X + (int)Math.Round(topLeft.X * scale), origin.Y + (int)Math.Round(topLeft.Y * scale),
            Math.Max(1, (int)Math.Round(ActualWidth * scale)), Math.Max(1, (int)Math.Round(ActualHeight * scale)));

        if (!force && bounds == _lastBounds) return;
        bool sizeChanged = bounds.Size != _lastBounds.Size;
        _lastBounds = bounds;
        // No owner relationship (GWLP_HWNDPARENT) anymore - that reproducibly crashed WinUI 3's own runtime
        // (a native fault inside Microsoft.UI.Xaml.dll, not this code). Without it, Windows won't automatically
        // keep this window stacked above the main one, so z-order is reasserted here instead. HWND_TOP (a null
        // handle), not mainHwnd, has to be the insert-after target: SetWindowPos's hWndInsertAfter places hWnd
        // immediately BEHIND the given window, so passing mainHwnd here was doing the opposite of what the name
        // suggested - putting the RDP surface behind the main window instead of in front of it. That bug didn't
        // show up on initial connect (a freshly shown window happens to start at the front regardless), only once
        // an actual resize called this and pushed it behind the main window, which looked identical to a blank
        // render but was really just full occlusion.
        NativeMethods.SetWindowPos(_rdpWindow.Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            NativeMethods.SWP_NOACTIVATE);

        // SmartSizing only rescales the picture the server already sent - the actual remote desktop resolution
        // never changes, so text stays blurry/tiny relative to the window and nothing re-lays-out server-side.
        // UpdateSessionDisplaySettings (RDPEDISP, the same live-resize mechanism modern mstsc.exe uses) asks the
        // server to genuinely resize the session. Debounced so a drag-resize doesn't fire a flood of requests -
        // only the size once it settles actually gets sent.
        if (sizeChanged && _connected) NotifyServerOfResizeDebounced(bounds.Width, bounds.Height);
    }

    private void NotifyServerOfResizeDebounced(int width, int height)
    {
        _resizeDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _resizeDebounceCts = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(400, cts.Token); }
            catch (OperationCanceledException) { return; }
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_detached || !_connected || _ax is null) return;
                try
                {
                    dynamic ocx = _ax.OcxObject;
                    double scale = XamlRoot?.RasterizationScale ?? 1.0;
                    // Physical size in mm, derived from pixel size at ~96 DPI/scale-factor 1.0 baseline - RDPEDISP
                    // uses this for DPI-aware rendering hints on the remote side; it isn't critical to get exact.
                    uint physW = (uint)Math.Round(width / (96.0 * scale) * 25.4);
                    uint physH = (uint)Math.Round(height / (96.0 * scale) * 25.4);
                    uint deviceScale = scale >= 1.6 ? 180u : scale >= 1.2 ? 140u : 100u; // must be exactly one of these three per MS-RDPEDISP
                    ocx.UpdateSessionDisplaySettings((uint)width, (uint)height, physW, physH, 0u, (uint)Math.Round(scale * 100), deviceScale);
                }
                catch { } // server/session may not support live resize (older Windows, some gateways) - SmartSizing still covers it
            });
        }, cts.Token);
    }

    private bool HasValidLayout => XamlRoot is not null && ActualWidth > 0 && ActualHeight > 0;

    /// <summary>Hosts MsRdpClient11NotSafeForScripting via its raw CLSID (no generated interop assembly) and wires
    /// its events through a hand-written dispinterface sink instead of an aximp-generated one.</summary>
    private sealed class RdpAxHost : AxHost
    {
        private ConnectionPointCookie? _cookie;
        private RdpEventSink? _sink;

        public event Action? RdpConnected;
        public event Action<int>? RdpDisconnected;
        public event Action<int>? RdpFatalError;

        public RdpAxHost() : base(ClsidMsRdpClient11) { }

        public object OcxObject => GetOcx()!;

        protected override void CreateSink()
        {
            _sink = new RdpEventSink(this);
            _cookie = new ConnectionPointCookie(GetOcx()!, _sink, typeof(IMsTscAxEvents));
        }

        protected override void DetachSink()
        {
            _cookie?.Disconnect();
            _cookie = null;
        }

        private sealed class RdpEventSink(RdpAxHost owner) : IMsTscAxEvents
        {
            public void OnConnecting() { }
            public void OnConnected() => owner.RdpConnected?.Invoke();
            public void OnLoginComplete() { }
            public void OnDisconnected(int discReason) => owner.RdpDisconnected?.Invoke(discReason);
            public void OnFatalError(int errorCode) => owner.RdpFatalError?.Invoke(errorCode);
            public void OnWarning(int warningCode) { }
        }
    }

    /// <summary>The RDP ActiveX control's event dispinterface - name, IID and DISPIDs read directly out of
    /// mstscax.dll's type library (LoadTypeLibEx), not guessed, since no aximp-generated definition is available.
    /// Only the events this control actually uses are declared; IDispatch invocation only needs the ones present.</summary>
    [ComVisible(true)]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    [Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
    public interface IMsTscAxEvents
    {
        [DispId(1)] void OnConnecting();
        [DispId(2)] void OnConnected();
        [DispId(3)] void OnLoginComplete();
        [DispId(4)] void OnDisconnected(int discReason);
        [DispId(10)] void OnFatalError(int errorCode);
        [DispId(11)] void OnWarning(int warningCode);
    }

    private static class NativeMethods
    {
        public const uint SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    }
}
