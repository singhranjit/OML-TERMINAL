using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OmlTerminal.Core.Vnc;
using Windows.System;

namespace OmlTerminal.App.Controls;

/// <summary>
/// Renders a VncClient's framebuffer and forwards keyboard/pointer input to it. First pass: polls the server for
/// updates at a fixed interval (simplest correct approach) rather than a continuous streaming loop.
/// </summary>
public sealed class VncControl : UserControl
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private WriteableBitmap? _bitmap;
    private VncClient? _client;
    private CancellationTokenSource? _pollCts;
    private byte _buttonMask;

    public event Action<string?>? Closed;

    public VncControl()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Content = _image;

        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        PointerReleased += OnPointerReleased;
        PointerMoved += OnPointerMoved;
        PointerWheelChanged += OnPointerWheel;
        KeyDown += (_, e) => SendKey(e, down: true);
        KeyUp += (_, e) => SendKey(e, down: false);
        CharacterReceived += OnCharacterReceived;
    }

    public async Task ConnectAsync(string host, int port, string password, CancellationToken ct = default)
    {
        var client = new VncClient();
        await client.ConnectAsync(host, port, password, ct).ConfigureAwait(false);
        await AttachAsync(client);
    }

    /// <summary>Connects through a WebSocket-tunneled RFB bridge (e.g. OML Labs' console proxy) instead of raw TCP.</summary>
    public async Task ConnectViaWebSocketAsync(Uri wsUri, string password,
        Func<System.Security.Cryptography.X509Certificates.X509Certificate2?, bool>? acceptCertificate, CancellationToken ct = default)
    {
        var client = new VncClient();
        await client.ConnectViaWebSocketAsync(wsUri, password, acceptCertificate, ct).ConfigureAwait(false);
        await AttachAsync(client);
    }

    private async Task AttachAsync(VncClient client)
    {
        _client = client;
        client.RectangleUpdated += (rect, pixels) => DispatcherQueue.TryEnqueue(() => Blit(rect, pixels));
        client.Closed += err => DispatcherQueue.TryEnqueue(() => Closed?.Invoke(err));

        await DispatcherQueue.EnqueueAsync(() =>
        {
            _bitmap = new WriteableBitmap(Math.Max(1, client.Width), Math.Max(1, client.Height));
            _image.Source = _bitmap;
        });

        _pollCts = new CancellationTokenSource();
        _ = Task.Run(() => PollLoop(client, _pollCts.Token));
    }

    private async Task PollLoop(VncClient client, CancellationToken ct)
    {
        client.RequestUpdate(incremental: false);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(80, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            client.RequestUpdate(incremental: true);
        }
    }

    private void Blit(VncRectangle rect, byte[] pixels)
    {
        if (_bitmap is null) return;
        try
        {
            using var stream = _bitmap.PixelBuffer.AsStream();
            int stride = _bitmap.PixelWidth * 4;
            int rowBytes = rect.Width * 4;
            for (int row = 0; row < rect.Height; row++)
            {
                stream.Seek((long)(rect.Y + row) * stride + rect.X * 4, SeekOrigin.Begin);
                stream.Write(pixels, row * rowBytes, rowBytes);
            }
            _bitmap.Invalidate();
        }
        catch { } // a rectangle arriving after resize/close races the bitmap; drop it rather than crash the UI thread
    }

    // ---------- input ----------

    private (double x, double y) ToRemote(Windows.Foundation.Point p)
    {
        if (_bitmap is null || ActualWidth <= 0 || ActualHeight <= 0) return (0, 0);
        double scale = Math.Min(ActualWidth / _bitmap.PixelWidth, ActualHeight / _bitmap.PixelHeight);
        double dispW = _bitmap.PixelWidth * scale, dispH = _bitmap.PixelHeight * scale;
        double offX = (ActualWidth - dispW) / 2, offY = (ActualHeight - dispH) / 2;
        return (Math.Clamp((p.X - offX) / scale, 0, _bitmap.PixelWidth - 1), Math.Clamp((p.Y - offY) / scale, 0, _bitmap.PixelHeight - 1));
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        e.Handled = true;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed) _buttonMask |= 1;
        if (props.IsMiddleButtonPressed) _buttonMask |= 2;
        if (props.IsRightButtonPressed) _buttonMask |= 4;
        SendPointer(e);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) { _buttonMask = 0; SendPointer(e); }
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e) => SendPointer(e);

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        var (x, y) = ToRemote(e.GetCurrentPoint(this).Position);
        byte mask = (byte)(delta > 0 ? 8 : 16); // VNC wheel-up/down button bits
        _client?.SendPointer(mask, (int)x, (int)y);
        _client?.SendPointer(0, (int)x, (int)y);
        e.Handled = true;
    }

    private void SendPointer(PointerRoutedEventArgs e)
    {
        var (x, y) = ToRemote(e.GetCurrentPoint(this).Position);
        _client?.SendPointer(_buttonMask, (int)x, (int)y);
    }

    private static readonly Dictionary<VirtualKey, uint> SpecialKeys = new()
    {
        [VirtualKey.Back] = X11Keysym.BackSpace, [VirtualKey.Tab] = X11Keysym.Tab, [VirtualKey.Enter] = X11Keysym.Return,
        [VirtualKey.Escape] = X11Keysym.Escape, [VirtualKey.Delete] = X11Keysym.Delete, [VirtualKey.Home] = X11Keysym.Home,
        [VirtualKey.End] = X11Keysym.End, [VirtualKey.Left] = X11Keysym.Left, [VirtualKey.Up] = X11Keysym.Up,
        [VirtualKey.Right] = X11Keysym.Right, [VirtualKey.Down] = X11Keysym.Down, [VirtualKey.PageUp] = X11Keysym.PageUp,
        [VirtualKey.PageDown] = X11Keysym.PageDown, [VirtualKey.Insert] = X11Keysym.Insert,
        [VirtualKey.Shift] = X11Keysym.ShiftL, [VirtualKey.Control] = X11Keysym.ControlL, [VirtualKey.Menu] = X11Keysym.AltL,
    };

    private void SendKey(KeyRoutedEventArgs e, bool down)
    {
        if (SpecialKeys.TryGetValue(e.Key, out var keysym))
        {
            _client?.SendKey(keysym, down);
            e.Handled = true;
            return;
        }
        if (e.Key is >= VirtualKey.F1 and <= VirtualKey.F12)
        {
            _client?.SendKey(X11Keysym.FunctionKey((int)e.Key - (int)VirtualKey.F1 + 1), down);
            e.Handled = true;
        }
        // Printable characters are sent from CharacterReceived instead, since VirtualKey doesn't carry shift-state text.
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (X11Keysym.FromChar(e.Character) is not { } keysym) return;
        e.Handled = true;
        _client?.SendKey(keysym, down: true);
        _client?.SendKey(keysym, down: false);
    }

    public void Detach()
    {
        _pollCts?.Cancel();
        _client?.Close();
        _client = null;
        Content = null;
    }
}

file static class DispatcherQueueExtensions
{
    public static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue dq, Action action)
    {
        var tcs = new TaskCompletionSource();
        if (!dq.TryEnqueue(() => { try { action(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } }))
            tcs.SetException(new InvalidOperationException("Failed to enqueue to dispatcher."));
        return tcs.Task;
    }
}
