using System;
using System.Net.Sockets;
using System.Threading;
using GwtEmulator.Protocol;

namespace GwtEmulator.Server;

public class GameSession
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly object _sendLock = new();   // v52: сериализация записи в сокет (см. Send)
    private readonly byte[] _recvBuffer = new byte[65545];
    private int _bytesReceived;
    private int _position;

    private bool _encrypt;
    private byte[]? _key;
    private readonly string _remoteEndPoint;
    private readonly string _localIpAddress;
    private int _disconnectState;

    public long PlayerGUID { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int Certification { get; set; }
    public bool IsConnected { get; private set; }
    public bool PlayMainSent { get; set; }
    public bool PlayMainDelivered { get; set; }
    public bool InitialTankInfoSent { get; set; }
    public bool HangarConsistencyChecked { get; set; }
    public string RemoteEndPoint => _remoteEndPoint;
    public string LocalIpAddress => _localIpAddress;

    public event Action<GameSession, IRequest>? MessageReceived;
    public event Action<GameSession>? Disconnected;

    public GameSession(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        try
        {
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            _client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            _client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            _client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 5);
        }
        catch { /* ignore on platforms/sockets that do not support fine-grained keepalive options */ }

        _remoteEndPoint = _client.Client?.RemoteEndPoint?.ToString() ?? "unknown";
        string localIp = "127.0.0.1";
        try
        {
            if (_client.Client?.LocalEndPoint is System.Net.IPEndPoint ep)
            {
                var addr = ep.Address;
                if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
                localIp = addr.ToString();
            }
        }
        catch { }
        _localIpAddress = localIp;

        _stream = client.GetStream();
        IsConnected = true;
    }

    public void EnableEncryption(long guid, int certification)
    {
        _key = CryptoHelper.GenerateKey(guid, certification);
        _encrypt = true;
    }

    public void SetEncryption(bool enabled) => _encrypt = enabled;

    public void ProcessReceivedData(int bytesReceived)
    {
        if (bytesReceived == 0)
        {
            HandleDisconnect();
            return;
        }

        _bytesReceived += bytesReceived;
        int available = _bytesReceived - _position;

        while (available > 0)
        {
            if (available < 9)
            {
                if (_position != 0)
                    Buffer.BlockCopy(_recvBuffer, _position, _recvBuffer, 0, available);
                _bytesReceived = available;
                _position = 0;
                break;
            }

            ushort magic = (ushort)(_recvBuffer[_position] | (_recvBuffer[_position + 1] << 8));
            if (magic != 0xAA55)
            {
                // Resync to the next 0xAA55 frame boundary instead of dropping the player on stray bytes (e.g. 0x0000).
                int syncPos = -1;
                for (int i = _position + 1; i <= _bytesReceived - 2; i++)
                {
                    if (_recvBuffer[i] == 0x55 && _recvBuffer[i + 1] == 0xAA)
                    {
                        syncPos = i;
                        break;
                    }
                }
                if (syncPos >= 0)
                {
                    int skipped = syncPos - _position;
                    Console.WriteLine($"[{_remoteEndPoint}] Resynced magic 0x{magic:X4} (skipped {skipped} byte(s))");
                    _position = syncPos;
                    available = _bytesReceived - _position;
                    continue;
                }

                bool keepTrailing55 = _recvBuffer[_bytesReceived - 1] == 0x55;
                int dropped = available - (keepTrailing55 ? 1 : 0);
                Console.WriteLine($"[{_remoteEndPoint}] Dropped {dropped} out-of-sync byte(s) starting 0x{magic:X4}");
                if (keepTrailing55)
                {
                    _recvBuffer[0] = 0x55;
                    _bytesReceived = 1;
                }
                else
                {
                    _bytesReceived = 0;
                }
                _position = 0;
                available = _bytesReceived;
                break;
            }

            ushort msgType = (ushort)(_recvBuffer[_position + 2] | (_recvBuffer[_position + 3] << 8));
            byte flags = _recvBuffer[_position + 4];
            bool isEncrypted = (flags & 1) == 1;
            ushort payloadLen = (ushort)(_recvBuffer[_position + 7] | (_recvBuffer[_position + 8] << 8));

            int frameLen = 9 + payloadLen;
            if (frameLen > _recvBuffer.Length)
            {
                Console.WriteLine($"[{_remoteEndPoint}] Oversized payload {payloadLen} for msgType={msgType}, resyncing...");
                _position += 2;
                available -= 2;
                continue;
            }

            if (payloadLen > available - 9)
            {
                if (_position != 0)
                    Buffer.BlockCopy(_recvBuffer, _position, _recvBuffer, 0, available);
                _bytesReceived = available;
                _position = 0;
                break;
            }

            int payloadOffset = _position + 9;
            byte[] payload = new byte[payloadLen];
            Buffer.BlockCopy(_recvBuffer, payloadOffset, payload, 0, payloadLen);

            if (isEncrypted && _key != null)
            {
                CryptoHelper.Decrypt(payload, payloadLen, _key);
            }

            var msgByteArray = new ByteArray(payload);
            var msgType2 = (TkMsgType)msgType;
            var message = MessageRegistry.CreateMessage(msgType2);

            if (message != null)
            {
                try
                {
                    message.ReadByte(msgByteArray);
                    MessageReceived?.Invoke(this, message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{_remoteEndPoint}] Packet handler error ({msgType2}): {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[{_remoteEndPoint}] Unknown message type: {msgType2} ({msgType})");
            }

            _position += frameLen;
            available = _bytesReceived - _position;
        }

        if (available == 0)
        {
            _bytesReceived = 0;
            _position = 0;
        }
    }

    public void SetupReceive()
    {
        if (!IsConnected) return;
        if (_bytesReceived >= _recvBuffer.Length)
        {
            Console.WriteLine($"[{_remoteEndPoint}] Receive buffer full, resetting offset");
            _bytesReceived = 0;
            _position = 0;
        }
        _stream.BeginRead(_recvBuffer, _bytesReceived, _recvBuffer.Length - _bytesReceived, OnDataReceived, null);
    }

    private void OnDataReceived(IAsyncResult ar)
    {
        try
        {
            int bytes = _stream.EndRead(ar);
            ProcessReceivedData(bytes);
            if (IsConnected) SetupReceive();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{_remoteEndPoint}] Socket receive closed: {ex.Message}");
            HandleDisconnect();
        }
    }

    public void Send(IRequest message)
    {
        if (!IsConnected) return;

        var ba = new ByteArray(1024);
        ba.Position = 0;
        ba.WriteUShort(0xAA55);
        ba.WriteUShort((ushort)message.MsgType);
        int payloadStartPos = 9;
        ba.Position = payloadStartPos;

        message.WriteByte(ba);

        if (message is PK_LC_LoginRet lr)
            Console.WriteLine($"[Session] LoginRet Certification={lr.Certification}, PlayerGUID={lr.PlayerGUID}, Ip={lr.Ip}, Port={lr.Port}");

        int payloadLen = (int)(ba.Position - payloadStartPos);
        byte flags = 0;
        ushort checkCode = 0;

        if (_encrypt && _key != null)
        {
            flags |= 1;
            CryptoHelper.Encrypt(ba.Bytes, _key, payloadStartPos, payloadStartPos + payloadLen);
        }

        flags |= 2;
        checkCode = CryptoHelper.ComputeCheckCode(ba.Bytes, payloadStartPos, payloadStartPos + payloadLen);

        int totalLen = (int)ba.Position;
        
        if (message is PK_LC_LoginRet)
        {
            var hex = BitConverter.ToString(ba.Bytes, 0, Math.Min(totalLen, 60)).Replace("-", " ");
            Console.WriteLine($"[Session] LoginRet bytes: {hex}");
        }
        
        ba.Position = 4;
        ba.WriteByte(flags);
        ba.WriteUShort(checkCode);
        ba.WriteUShort((ushort)payloadLen);
        ba.Position = 0;

        try
        {
            // v52: параллельные Send из разных потоков (выстрелы ботов из пула + основной цикл)
            // раньше писали в поток вперемешку и корёжили кадры на проводе — из-за этого,
            // в частности, отложенная декаль пробития «терялась». Сериализуем саму запись.
            lock (_sendLock)
            {
                _stream.Write(ba.Bytes, 0, totalLen);
                _stream.Flush();
            }
            Console.WriteLine($"[Session] Sent {totalLen} bytes, msgType={message.MsgType}");
        }
        catch
        {
            HandleDisconnect();
        }
    }

    private void HandleDisconnect()
    {
        if (Interlocked.CompareExchange(ref _disconnectState, 1, 0) != 0) return;
        IsConnected = false;
        try { _client.Close(); } catch { }
        try { Disconnected?.Invoke(this); } catch { }
    }

    public void Disconnect() => HandleDisconnect();
}
