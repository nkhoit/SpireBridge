using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Godot;
using MegaCrit.Sts2.Core.Modding;

namespace SpireBridge;

[ModInitializer("Initialize")]
public static class SpireBridgeMod
{
    private const int Port = 38642;
    private static HttpListener? _httpListener;
    private static CancellationTokenSource? _cts;
    private static readonly List<WebSocket> _clients = new();
    private static readonly object _clientLock = new();
    private static readonly List<(WebSocket client, string message)> _pendingMessages = new();
    private static readonly object _pendingLock = new();

    public static void Initialize()
    {
        Log("SpireBridge v0.1.0 initializing...");
        _cts = new CancellationTokenSource();

        // Start WebSocket server on a background thread
        var thread = new Thread(RunServer) { IsBackground = true, Name = "SpireBridge-WS" };
        thread.Start();

        // Register a process callback on the main thread to handle queued messages
        var timer = new Godot.Timer();
        timer.WaitTime = 0.05; // 50ms tick
        timer.Autostart = true;
        timer.Timeout += ProcessPendingMessages;

        // Add timer to scene tree
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.Root.CallDeferred("add_child", timer);

        Log("SpireBridge initialized. WebSocket server starting on port " + Port);

        // Subscribe to game events for push-based state updates (deferred to ensure singletons exist)
        tree.Root.CallDeferred("call_deferred", Callable.From(() =>
        {
            // Delay subscription slightly to let game singletons initialize
            var subscribeTimer = new Godot.Timer();
            subscribeTimer.WaitTime = 2.0;
            subscribeTimer.OneShot = true;
            subscribeTimer.Timeout += () =>
            {
                GameEventBridge.Subscribe();
                subscribeTimer.QueueFree();
            };
            tree.Root.AddChild(subscribeTimer);
            subscribeTimer.Start();
        }));
    }

    private static void RunServer()
    {
        try
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _httpListener.Start();
            Log($"WebSocket server listening on ws://127.0.0.1:{Port}/");

            while (!_cts!.Token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _httpListener.GetContext();
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }

                if (ctx.Request.IsWebSocketRequest)
                {
                    _ = HandleWebSocketAsync(ctx);
                }
                else
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                }
            }
        }
        catch (Exception ex)
        {
            Log($"WebSocket server error: {ex}");
        }
    }

    private static async Task HandleWebSocketAsync(HttpListenerContext httpCtx)
    {
        WebSocket ws;
        try
        {
            var wsCtx = await httpCtx.AcceptWebSocketAsync(null);
            ws = wsCtx.WebSocket;
        }
        catch (Exception ex)
        {
            Log($"WebSocket accept error: {ex}");
            return;
        }

        lock (_clientLock) { _clients.Add(ws); }
        Log("Client connected");

        var buffer = new byte[8192];
        try
        {
            while (ws.State == WebSocketState.Open && !_cts!.Token.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    lock (_pendingLock)
                    {
                        _pendingMessages.Add((ws, msg));
                    }
                }
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_clientLock) { _clients.Remove(ws); }
            Log("Client disconnected");
        }
    }

    /// <summary>
    /// Called on the main thread via Godot Timer to process queued WebSocket messages.
    /// </summary>
    private static void ProcessPendingMessages()
    {
        List<(WebSocket client, string message)> batch;
        lock (_pendingLock)
        {
            if (_pendingMessages.Count == 0) return;
            batch = new List<(WebSocket, string)>(_pendingMessages);
            _pendingMessages.Clear();
        }

        foreach (var (client, message) in batch)
        {
            try
            {
                var response = CommandHandler.Handle(message);
                SendAsync(client, response);
            }
            catch (Exception ex)
            {
                var error = JsonSerializer.Serialize(new { error = ex.Message });
                SendAsync(client, error);
            }
        }
    }

    private static async void SendAsync(WebSocket ws, string message)
    {
        if (ws.State != WebSocketState.Open) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { /* client gone */ }
    }

    /// <summary>Broadcast a message to all connected clients.</summary>
    public static void BroadcastToClients(string message) => Broadcast(message);
    public static void Broadcast(string message)
    {
        List<WebSocket> snapshot;
        lock (_clientLock) { snapshot = new List<WebSocket>(_clients); }
        foreach (var ws in snapshot)
        {
            SendAsync(ws, message);
        }
    }

    /// <summary>Schedule an action to run on the main thread after a delay.</summary>
    public static void ScheduleAction(float delaySec, Action action)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        // Use SceneTree.CreateTimer which is more reliable than manually creating Timer nodes
        var sceneTimer = tree.CreateTimer(delaySec);
        sceneTimer.Timeout += () =>
        {
            try 
            { 
                Log($"ScheduleAction: timer fired after {delaySec}s");
                action(); 
            }
            catch (Exception ex) { Log($"ScheduleAction error: {ex.Message}"); }
        };
    }

    internal static void Log(string msg)
    {
        GD.Print($"[SpireBridge] {msg}");
    }
}
