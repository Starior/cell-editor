using System.IO;
using System.Net;
using System.Text.Json;

namespace CellEditor;

/// <summary>
/// Local API so an agent on this machine can see the open file and the live log.
/// Listens on http://127.0.0.1:8932/ only.
/// </summary>
public sealed class AgentServer : IDisposable
{
    public const int Port = 8932;

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    readonly HttpListener listener = new();
    readonly CancellationTokenSource cancel = new();
    readonly EditorSession session;
    readonly EditorLog log;
    readonly Func<string, EditorSnapshot> openFile;

    public AgentServer(EditorSession session, EditorLog log, Func<string, EditorSnapshot> openFile)
    {
        this.session = session;
        this.log = log;
        this.openFile = openFile;
        listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
    }

    public void Start()
    {
        listener.Start();
        Task.Run(Loop);
    }

    public void Dispose()
    {
        cancel.Cancel();
        listener.Close();
    }

    async Task Loop()
    {
        while (!cancel.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancel.Token);
            }
            catch (Exception) when (cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.Error("Agent connection failed: " + ex.Message);
                return;
            }

            _ = Task.Run(() => Handle(context));
        }
    }

    void Handle(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var method = context.Request.HttpMethod;
            if (method == "GET" && path == "/api/status")
            {
                WriteJson(context, session.Snapshot);
                return;
            }

            if (method == "GET" && path == "/api/logs")
            {
                var after = 0L;
                var raw = context.Request.QueryString["after"];
                if (!string.IsNullOrEmpty(raw))
                {
                    long.TryParse(raw, out after);
                }

                WriteJson(context, new { lines = log.After(after) });
                return;
            }

            if (method == "POST" && path == "/api/open")
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = reader.ReadToEnd();
                var request = JsonSerializer.Deserialize<OpenRequest>(body, Json);
                if (request == null || string.IsNullOrWhiteSpace(request.Path))
                {
                    WriteJson(context, new { error = "Missing path." }, 400);
                    return;
                }

                var snapshot = openFile(request.Path);
                WriteJson(context, snapshot);
                return;
            }

            if (method == "GET" && (path == "/" || path == "/api"))
            {
                WriteJson(context, new
                {
                    name = "Cell Editor",
                    status = "/api/status",
                    logs = "/api/logs",
                    open = "POST /api/open { \"path\": \"...\" }",
                });
                return;
            }

            WriteJson(context, new { error = "Not found." }, 404);
        }
        catch (Exception ex)
        {
            try
            {
                WriteJson(context, new { error = ex.Message }, 500);
            }
            catch (Exception)
            {
                // The client went away.
            }
        }
    }

    static void WriteJson(HttpListenerContext context, object body, int status = 200)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    sealed class OpenRequest
    {
        public string? Path { get; set; }
    }
}
