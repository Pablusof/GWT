using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace GwtEmulator.Server;

// Serves the real game's AssetBundles folder over HTTP at /AssetBundles/...,
// matching exactly the path the client requests (PathManager.AssetbundleBaseURL
// => "http://" + AssetIP + "/AssetBundles/" + <relative path>.tk in the original
// client). The original CDN this used to point at (images.tanks.mail.ru) is no
// longer reachable, which is why tank models rendered with no skin texture
// (solid black) even though the model geometry itself loaded fine from a local
// Unity cache. Pointing ClientConfig.tk's AssetIP at this server instead (see
// setup.ps1) lets the client load those same files straight from disk.
public class AssetServer
{
    private readonly int _port;
    private readonly string _rootDir;
    private TcpListener? _listener;
    private bool _running;

    public AssetServer(int port, string rootDir)
    {
        _port = port;
        _rootDir = Path.GetFullPath(rootDir);
    }

    public void Start()
    {
        if (!Directory.Exists(_rootDir))
        {
            Console.WriteLine($"[Asset] WARNING: AssetBundles directory not found: {_rootDir}");
            Console.WriteLine("[Asset] Tank models will still render without skins until this path is fixed.");
        }

        _running = true;
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        Console.WriteLine($"[Asset] Serving '{_rootDir}' on http://0.0.0.0:{_port}/AssetBundles/ (all interfaces)");
        Task.Run(() => ListenLoop());
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
    }

    private async Task ListenLoop()
    {
        while (_running)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClient(client));
            }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }
        }
    }

    private async Task HandleClient(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            try
            {
                var reader = new StreamReader(stream, Encoding.UTF8);
                string? requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(requestLine)) return;

                // Drain headers (we don't need any of them for a GET file server).
                string? headerLine;
                while (!string.IsNullOrEmpty(headerLine = await reader.ReadLineAsync())) { }

                var parts = requestLine.Split(' ');
                string path = parts.Length > 1 ? parts[1] : "/";
                int qIdx = path.IndexOf('?');
                if (qIdx >= 0) path = path.Substring(0, qIdx);

                Console.WriteLine($"[Asset] GET {path}");
                await ServeFile(stream, path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Asset] Error handling request: {ex.Message}");
            }
        }
    }

    private async Task ServeFile(NetworkStream stream, string urlPath)
    {
        const string prefix = "/AssetBundles/";
        if (!urlPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            await WriteResponse(stream, 404, "text/plain", Encoding.UTF8.GetBytes("404 Not Found"));
            return;
        }

        string relative = Uri.UnescapeDataString(urlPath.Substring(prefix.Length));
        // Basic traversal guard: resolve and verify the final path still sits under _rootDir.
        string fullPath = Path.GetFullPath(Path.Combine(_rootDir, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(_rootDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            Console.WriteLine($"[Asset] 404: {relative}");
            await WriteResponse(stream, 404, "text/plain", Encoding.UTF8.GetBytes("404 Not Found"));
            return;
        }

        byte[] body = await File.ReadAllBytesAsync(fullPath);
        await WriteResponse(stream, 200, "application/octet-stream", body);
    }

    private static async Task WriteResponse(NetworkStream stream, int statusCode, string contentType, byte[] body)
    {
        string statusText = statusCode == 200 ? "OK" : "Not Found";
        string header = $"HTTP/1.1 {statusCode} {statusText}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\nAccess-Control-Allow-Origin: *\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
        await stream.WriteAsync(body, 0, body.Length);
        await stream.FlushAsync();
    }
}
