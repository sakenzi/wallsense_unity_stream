using UnityEngine;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Встроенный MJPEG HTTP сервер прямо в Unity.
/// Работает на PC/Mac — на Quest нужны доп. права.
/// </summary>
public class DroneMJPEGServer : MonoBehaviour
{
    [Header("Сервер")]
    public int port = 8081;

    [Header("Ссылки")]
    public DroneCameraController droneCameraController;

    [Header("Качество")]
    [Range(1, 30)] public int fps         = 20;
    [Range(1, 100)] public int jpegQuality = 55;

    [Header("Debug")]
    public bool showLogs = true;

    // ── Внутреннее ───────────────────────────────────────────────────
    private HttpListener   listener;
    private Thread         listenerThread;
    private volatile bool  running = false;

    // Последний JPEG кадр — shared между Unity thread и HTTP thread
    private volatile byte[] latestJpeg = null;
    private readonly object frameLock  = new object();

    // Список активных клиентских ответов
    private readonly List<HttpListenerResponse> clients = new List<HttpListenerResponse>();
    private readonly object clientLock = new object();

    private const string BOUNDARY = "mjpegboundary";

    // ─────────────────────────────────────────────────────────────────

    void Start()
    {
        StartServer();
        StartCoroutine(CaptureLoop());
    }

    void OnDestroy()
    {
        StopServer();
    }

    // ── HTTP сервер ───────────────────────────────────────────────────

    void StartServer()
    {
        try
        {
            listener = new HttpListener();
            // Слушаем на ВСЕХ интерфейсах — работает и по localhost и по Wi-Fi IP
            listener.Prefixes.Add($"http://+:{port}/");
            listener.Start();

            running = true;
            listenerThread = new Thread(ListenLoop) { IsBackground = true };
            listenerThread.Start();

            // Показываем все IP адреса устройства
            string localIP = GetLocalIP();
            Debug.Log($"[MJPEGServer] ✓ Запущен на порту {port}");
            Debug.Log($"[MJPEGServer] PC/Link:  http://localhost:{port}/stream");
            Debug.Log($"[MJPEGServer] Wi-Fi:    http://{localIP}:{port}/stream  ← используй в React");
            localIPAddress = localIP;
        }
        catch (Exception e)
        {
            // http://+:port/ требует прав на Windows — fallback на localhost
            Debug.LogWarning($"[MJPEGServer] Не удалось слушать на всех интерфейсах: {e.Message}");
            Debug.LogWarning("[MJPEGServer] Fallback на localhost (только для Link кабеля)");
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");

                // Пробуем добавить и реальный IP
                string detectedIP = GetLocalIP();
                if (detectedIP != "127.0.0.1")
                {
                    try { listener.Prefixes.Add($"http://{detectedIP}:{port}/"); }
                    catch { /* игнорируем если не удалось */ }
                }

                listener.Start();
                running = true;
                listenerThread = new Thread(ListenLoop) { IsBackground = true };
                listenerThread.Start();
                localIPAddress = detectedIP;

                Debug.Log($"[MJPEGServer] ✓ Запущен (fallback режим)");
                Debug.Log($"[MJPEGServer] localhost : http://localhost:{port}/stream");
                Debug.Log($"[MJPEGServer] Wi-Fi IP : http://{detectedIP}:{port}/stream");
                Debug.Log($"[MJPEGServer] >>> Скопируй этот IP в React <<<");
            }
            catch (Exception e2)
            {
                Debug.LogError($"[MJPEGServer] Не удалось запустить: {e2.Message}");
                Debug.LogError("[MJPEGServer] Попробуй запустить Unity от имени администратора");
            }
        }
    }

    private string localIPAddress = "unknown";

    string GetLocalIP()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            return ((IPEndPoint)socket.LocalEndPoint).Address.ToString();
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    void StopServer()
    {
        running = false;

        lock (clientLock)
        {
            foreach (var r in clients)
                try { r.Close(); } catch { }
            clients.Clear();
        }

        try { listener?.Stop(); listener?.Close(); } catch { }
        listenerThread?.Join(500);

        Log("Сервер остановлен");
    }

    void ListenLoop()
    {
        while (running)
        {
            try
            {
                var ctx = listener.GetContext(); 
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
            }
            catch (HttpListenerException)
            {
                break; // сервер остановлен
            }
            catch (Exception e)
            {
                if (running) Debug.LogWarning($"[MJPEGServer] ListenLoop: {e.Message}");
            }
        }
    }

    void HandleRequest(HttpListenerContext ctx)
    {
        var req  = ctx.Request;
        var resp = ctx.Response;

        // CORS — разрешаем Electron/браузер
        resp.AddHeader("Access-Control-Allow-Origin",  "*");
        resp.AddHeader("Access-Control-Allow-Methods", "GET, OPTIONS");
        resp.AddHeader("Cache-Control", "no-cache, no-store");

        if (req.HttpMethod == "OPTIONS")
        {
            resp.StatusCode = 200;
            resp.Close();
            return;
        }

        var path = req.Url.AbsolutePath.ToLower();

        if (path == "/stream")
        {
            ServeStream(resp);
        }
        else if (path == "/snapshot")
        {
            ServeSnapshot(resp);
        }
        else if (path == "/status")
        {
            ServeStatus(resp);
        }
        else
        {
            resp.StatusCode = 404;
            resp.Close();
        }
    }

    /// <summary>
    /// MJPEG поток — клиент держит соединение открытым,
    /// мы шлём кадры один за другим.
    /// </summary>
    void ServeStream(HttpListenerResponse resp)
    {
        resp.ContentType = $"multipart/x-mixed-replace; boundary={BOUNDARY}";
        resp.StatusCode  = 200;
        resp.SendChunked = true;

        lock (clientLock) clients.Add(resp);
        Log($"Браузер подключился (всего: {clients.Count})");

        var stream = resp.OutputStream;
        byte[] lastSent = null;

        try
        {
            while (running)
            {
                byte[] frame;
                lock (frameLock) frame = latestJpeg;

                // Шлём только если кадр новый
                if (frame != null && !ReferenceEquals(frame, lastSent))
                {
                    lastSent = frame;

                    var header = System.Text.Encoding.ASCII.GetBytes(
                        $"--{BOUNDARY}\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n"
                    );

                    stream.Write(header, 0, header.Length);
                    stream.Write(frame, 0, frame.Length);
                    stream.Write(new byte[] { (byte)'\r', (byte)'\n' }, 0, 2);
                    stream.Flush();
                }
                else
                {
                    Thread.Sleep(5); // ждём новый кадр
                }
            }
        }
        catch
        {
            // Клиент отключился — нормально
        }
        finally
        {
            lock (clientLock) clients.Remove(resp);
            try { resp.Close(); } catch { }
            Log($"Браузер отключился (всего: {clients.Count})");
        }
    }

    /// <summary>
    /// Одиночный JPEG снимок — GET /snapshot
    /// </summary>
    void ServeSnapshot(HttpListenerResponse resp)
    {
        byte[] frame;
        lock (frameLock) frame = latestJpeg;

        if (frame == null) { resp.StatusCode = 503; resp.Close(); return; }

        resp.ContentType   = "image/jpeg";
        resp.ContentLength64 = frame.Length;
        resp.OutputStream.Write(frame, 0, frame.Length);
        resp.Close();
    }

    /// <summary>
    /// JSON статус — GET /status
    /// </summary>
    void ServeStatus(HttpListenerResponse resp)
    {
        byte[] frame;
        lock (frameLock) frame = latestJpeg;

        var json = System.Text.Encoding.UTF8.GetBytes(
            $"{{\"running\":true,\"clients\":{clients.Count},\"hasFrame\":{(frame != null ? "true" : "false")},\"port\":{port},\"ip\":\"{localIPAddress}\"}}"
        );

        resp.ContentType = "application/json";
        resp.ContentLength64 = json.Length;
        resp.OutputStream.Write(json, 0, json.Length);
        resp.Close();
    }

    // ── Захват кадров (Unity main thread) ────────────────────────────

    IEnumerator CaptureLoop()
    {
        var wait = new WaitForSeconds(1f / fps);

        while (true)
        {
            yield return new WaitForEndOfFrame();

            var rt = droneCameraController?.GetRenderTexture();
            if (rt != null)
            {
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;

                byte[] jpeg = tex.EncodeToJPG(jpegQuality);
                Destroy(tex);

                lock (frameLock) latestJpeg = jpeg;
            }

            yield return wait;
        }
    }

    void Log(string msg)
    {
        if (showLogs) Debug.Log($"[MJPEGServer] {msg}");
    }

    // Public API
    public string GetStreamUrl()      => $"http://localhost:{port}/stream";
    public string GetStreamUrlByIP()  => $"http://{localIPAddress}:{port}/stream";
    public string GetSnapshotUrl()    => $"http://localhost:{port}/snapshot";
    public string GetLocalIPAddress() => localIPAddress;
    public int    ClientCount         => clients.Count;
}