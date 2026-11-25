using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace GwtEmulator.Server;

public abstract class GameServer
{
    private TcpListener? _listener;
    private readonly string _name;
    private readonly int _port;

    protected readonly Dictionary<string, GameSession> _sessions = new();

    public string Name => _name;
    public int Port => _port;
    public int ConnectedCount => _sessions.Count;

    protected GameServer(string name, int port)
    {
        _name = name;
        _port = port;
    }

    public virtual void Start(string bindIp = "0.0.0.0")
    {
        _listener = new TcpListener(IPAddress.Parse(bindIp), _port);
        _listener.Start();
        Console.WriteLine($"[{_name}] Listening on {bindIp}:{_port}");
        _listener.BeginAcceptTcpClient(OnAcceptClient, null);
    }

    private void OnAcceptClient(IAsyncResult ar)
    {
        try
        {
            var client = _listener!.EndAcceptTcpClient(ar);
            var session = CreateSession(client);
            session.MessageReceived += OnMessageReceived;
            session.Disconnected += OnSessionDisconnected;

            lock (_sessions)
                _sessions[session.RemoteEndPoint] = session;

            Console.WriteLine($"[{_name}] Client connected: {session.RemoteEndPoint} (total: {ConnectedCount})");

            OnClientConnected(session);
            session.SetupReceive();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{_name}] Accept error: {ex.Message}");
        }

        try
        {
            _listener!.BeginAcceptTcpClient(OnAcceptClient, null);
        }
        catch { }
    }

    protected abstract GameSession CreateSession(TcpClient client);
    protected abstract void OnClientConnected(GameSession session);
    protected abstract void OnMessageReceived(GameSession session, Protocol.IRequest message);

    private void OnSessionDisconnected(GameSession session)
    {
        var endPoint = "unknown";
        try { endPoint = session.RemoteEndPoint; } catch { }
        lock (_sessions)
            _sessions.Remove(endPoint);
        Console.WriteLine($"[{_name}] Client disconnected: {endPoint} (total: {ConnectedCount})");
        OnClientDisconnected(session);
    }

    protected virtual void OnClientDisconnected(GameSession session) { }

    public virtual void Stop()
    {
        _listener?.Stop();
        lock (_sessions)
        {
            foreach (var s in _sessions.Values)
                s.Disconnect();
            _sessions.Clear();
        }
        Console.WriteLine($"[{_name}] Stopped");
    }
}
