using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using WarpforgeOpenServer;

// Stand-in for the PlayFab title A3CCB that Warpforge talked to. The BepInEx plugin rewrites
// https://a3ccb.playfabapi.com/<Api>/<Method> to this server.
//
// Every request is logged to the console and logs/requests.log. Unknown methods return an empty
// success and are flagged UNHANDLED, so the log shows what to implement next.

const string TitleId = "A3CCB";

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls("https://localhost:5443", "http://localhost:5080");
var app = builder.Build();

var root = builder.Environment.ContentRootPath;
var dataDir = Path.Combine(root, "data");
var logDir = Path.Combine(root, "logs");
Directory.CreateDirectory(logDir);
var requestLog = new StreamWriter(Path.Combine(logDir, "requests.log"), append: true, Encoding.UTF8) { AutoFlush = true };
var logLock = new object();

var store = new PlayerStore(dataDir);
var api = new Api(store, TitleId);
var handlers = api.Handlers();

void Log(string text, ConsoleColor color = ConsoleColor.Gray)
{
    var line = $"[{DateTime.Now:HH:mm:ss.fff}] {text}";
    lock (logLock)
    {
        requestLog.WriteLine(line);
        Console.ForegroundColor = color;
        Console.WriteLine(line);
        Console.ResetColor();
    }
}

// A file in data/<folder>/<name>.json, if present, overrides the built-in behaviour
JsonNode? Canned(string folder, string? name)
{
    if (string.IsNullOrEmpty(name)) return null;
    var path = Path.Combine(dataDir, folder, name + ".json");
    return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
}

app.MapPost("/{apiName}/{method}", async (HttpContext ctx, string apiName, string method) =>
{
    var body = await ReadBody(ctx.Request);
    JsonObject req;
    try { req = string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body)!.AsObject(); }
    catch { req = new JsonObject(); }

    Log($"--> {apiName}/{method} {Shorten(body)}", ConsoleColor.Cyan);

    JsonNode? data;
    string tag;
    if (method.Equals("ExecuteCloudScript", StringComparison.OrdinalIgnoreCase) ||
        method.Equals("ExecuteFunction", StringComparison.OrdinalIgnoreCase))
    {
        var fn = (string?)req["FunctionName"];
        var canned = Canned("cloudscript", fn);
        var result = canned ?? api.BuiltInCloudScript(fn, req["FunctionParameter"] as JsonObject);
        data = Api.CloudScriptResult(method, fn, result);
        tag = canned != null ? $"cloudscript {fn} (canned)"
            : result != null ? $"cloudscript {fn}"
            : $"UNHANDLED cloudscript {fn}";
    }
    else if (Canned("responses", method) is { } cannedResponse)
    {
        data = cannedResponse;
        tag = "canned";
    }
    else if (handlers.TryGetValue(method, out var handler))
    {
        data = handler(req);
        tag = "ok";
    }
    else
    {
        data = new JsonObject();
        tag = "UNHANDLED";
    }

    var envelope = new JsonObject { ["code"] = 200, ["status"] = "OK", ["data"] = data };
    var json = envelope.ToJsonString();
    Log($"<-- {apiName}/{method} [{tag}] {Shorten(json)}", tag.StartsWith("UNHANDLED") ? ConsoleColor.Yellow : ConsoleColor.Green);

    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync(json);
});

// Unity Cloud Content Delivery stand-in for Addressables. The plugin redirects
// *.unity3dusercontent.com here; files come from data/ccd (the last catalog the game cached, plus
// the remote bundles recovered from Unity's bundle cache).
//   GET /client_api/v1/environments/<env>/buckets/<bucket>/release_by_badge/<badge>/entry_by_path/content/?path=/<file>
// Bundles are requested by base name (allcards_assets_all.bundle) but stored with their hash suffix,
// so match on prefix.
var ccdDir = Path.Combine(dataDir, "ccd");
app.MapGet("/client_api/{**rest}", async (HttpContext ctx) =>
{
    var requested = Path.GetFileName(ctx.Request.Query["path"].ToString().TrimStart('/'));
    string? file = null;
    if (!string.IsNullOrEmpty(requested) && Directory.Exists(ccdDir))
    {
        var exact = Path.Combine(ccdDir, requested);
        if (File.Exists(exact)) file = exact;
        else if (requested.EndsWith(".bundle"))
        {
            var stem = Path.GetFileNameWithoutExtension(requested);
            file = Directory.GetFiles(ccdDir, stem + "_*.bundle").FirstOrDefault();
        }
    }

    if (file == null)
    {
        Log($"CCD 404 {requested}", ConsoleColor.Yellow);
        ctx.Response.StatusCode = 404;
        return;
    }
    Log($"CCD {requested} -> {Path.GetFileName(file)}", ConsoleColor.Green);
    ctx.Response.ContentType = file.EndsWith(".hash") || file.EndsWith(".json") ? "text/plain" : "application/octet-stream";
    await ctx.Response.SendFileAsync(file);
});

// Anything that isn't POST /<Api>/<Method>
app.MapFallback(async ctx =>
{
    Log($"??? {ctx.Request.Method} {ctx.Request.Path}{ctx.Request.QueryString} {Shorten(await ReadBody(ctx.Request))}", ConsoleColor.Red);
    ctx.Response.StatusCode = 404;
});

Log($"WarpforgeOpenServer (PlayFab title {TitleId}) listening on https://localhost:5443 and http://localhost:5080; data in {dataDir}", ConsoleColor.White);
app.Run();

static async Task<string> ReadBody(HttpRequest request)
{
    Stream stream = request.Body;
    if (request.Headers.ContentEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
        stream = new GZipStream(stream, CompressionMode.Decompress);
    using var reader = new StreamReader(stream, Encoding.UTF8);
    return await reader.ReadToEndAsync();
}

// Steam tickets are login credentials; keep them out of the log
static string Shorten(string s)
{
    s = System.Text.RegularExpressions.Regex.Replace(s, "(\"SteamTicket\"\\s*:\\s*\")[0-9A-Fa-f]+", "$1<redacted>");
    return s.Length <= 2000 ? s : s[..2000] + $"...(+{s.Length - 2000} chars)";
}
