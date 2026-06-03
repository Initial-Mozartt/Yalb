using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Yalb
{
    internal static class StartpageServer
    {
        private static HttpListener? _listener;
        private static Thread? _thread;
        private static string? _rootPath;
        private static volatile bool _running;
        private const string Prefix = "http://localhost:3000/";

        public static bool IsRunning => _running;
        public static string BaseUrl => Prefix.TrimEnd('/');

        public static void Start(string rootPath)
        {
            if (_running) return;
            _rootPath = rootPath;
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add(Prefix);
                _listener.Start();
                _running = true;
                _thread = new Thread(ServerLoop) { IsBackground = true, Name = "StartpageServer" };
                _thread.Start();
            }
            catch (Exception ex)
            {
                _running = false;
                try { _listener?.Close(); } catch { }
                YalbLogger.Error("StartpageServer.Start failed", ex);
            }
        }

        public static void Stop()
        {
            try
            {
                _running = false;
                try { _listener?.Stop(); } catch { }
                try { _thread?.Join(2000); } catch { }
                try { _listener?.Close(); } catch { }
            }
            catch { }
        }

        private static void ServerLoop()
        {
            if (_listener == null) return;
            while (_running)
            {
                HttpListenerContext? ctx = null;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch
                {
                    break;
                }

                try
                {
                    HandleContext(ctx);
                }
                catch (Exception ex)
                {
                    try
                    {
                        ctx.Response.StatusCode = 500;
                        var msg = Encoding.UTF8.GetBytes("Server error");
                        ctx.Response.OutputStream.Write(msg, 0, msg.Length);
                        ctx.Response.Close();
                    }
                    catch { }
                    YalbLogger.Error("StartpageServer request failed", ex);
                }
            }
        }

        private static void HandleContext(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            string urlPath = Uri.UnescapeDataString(req.Url?.AbsolutePath ?? "/");
            if (urlPath.StartsWith("/")) urlPath = urlPath.Substring(1);
            if (string.IsNullOrEmpty(urlPath)) urlPath = "index.html";

            // prevent traversal
            if (urlPath.Contains("..")) { res.StatusCode = 400; res.Close(); return; }

            var filePath = Path.Combine(_rootPath ?? string.Empty, urlPath.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(filePath)) filePath = Path.Combine(filePath, "index.html");

            if (!File.Exists(filePath))
            {
                res.StatusCode = 404;
                var notFound = Encoding.UTF8.GetBytes("Not Found");
                res.OutputStream.Write(notFound, 0, notFound.Length);
                res.Close();
                return;
            }

            try
            {
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                res.ContentType = ext switch
                {
                    ".html" => "text/html; charset=utf-8",
                    ".js" => "application/javascript; charset=utf-8",
                    ".css" => "text/css; charset=utf-8",
                    ".json" => "application/json; charset=utf-8",
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".svg" => "image/svg+xml",
                    ".ico" => "image/x-icon",
                    _ => "application/octet-stream"
                };

                using var fs = File.OpenRead(filePath);
                res.ContentLength64 = fs.Length;
                res.SendChunked = false;
                fs.CopyTo(res.OutputStream);
                res.OutputStream.Flush();
                res.Close();
            }
            catch
            {
                try { res.StatusCode = 500; res.Close(); } catch { }
            }
        }
    }
}
