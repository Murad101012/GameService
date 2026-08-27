using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.StaticFiles;
using PhonesBridge.Services.Definitions;

namespace PhonesBridge.Services;

public class WebDavMiddleware
{
    private readonly RequestDelegate _next;
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    public WebDavMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ConnectionManager connectionManager, IHubContext<CommunicateSignalR> hubContext, SettingsManager settingsManager)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length >= 2 && 
            segments[0].Equals("communicate", StringComparison.OrdinalIgnoreCase) && 
            !segments[1].Equals("negotiate", StringComparison.OrdinalIgnoreCase))
        {
            // Device URLs (Dolphin WebDAV / HTTPS) are disabled by default: reject
            // every request to these endpoints so the links serve nothing.
            if (!settingsManager.IsWebDavEnabled)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            string deviceName = WebUtility.UrlDecode(segments[1]);

            if (connectionManager.TryGetConnectionId(deviceName, out string? connectionId))
            {
                bool isLowRes = false;
                int pathStartIndex = 2;
                string mode = "full";

                if (segments.Length >= 3 && 
                    (segments[2].Equals("lowres", StringComparison.OrdinalIgnoreCase) || 
                     segments[2].Equals("full", StringComparison.OrdinalIgnoreCase)))
                {
                    mode = segments[2].ToLowerInvariant();
                    isLowRes = mode == "lowres";
                    pathStartIndex = 3;
                }

                string relativePath = string.Join('/', segments.Skip(pathStartIndex).Select(WebUtility.UrlDecode));
                string method = context.Request.Method.ToUpperInvariant();

                if (method == "OPTIONS")
                {
                    HandleOptions(context);
                    return;
                }
                if (method == "PROPFIND")
                {
                    await HandlePropfindAsync(context, hubContext, connectionManager, deviceName, mode, relativePath, isLowRes);
                    return;
                }
                if (method == "GET" || method == "HEAD")
                {
                    await HandleGetAsync(context, hubContext, connectionManager, deviceName, relativePath, isLowRes);
                    return;
                }
            }
        }

        await _next(context);
    }

    private static void HandleOptions(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers["DAV"] = "1";
        context.Response.Headers["Allow"] = "GET, HEAD, OPTIONS, PROPFIND";
        context.Response.Headers["MS-Author-Via"] = "DAV";
    }

    private static async Task HandlePropfindAsync(
        HttpContext context, 
        IHubContext<CommunicateSignalR> hubContext, 
        ConnectionManager connectionManager, 
        string deviceName, 
        string mode, 
        string relativePath, 
        bool isLowRes)
    {
        string depth = context.Request.Headers["Depth"].FirstOrDefault() ?? "1";
        bool isDepthZero = depth == "0";

        try
        {
            if (!connectionManager.TryGetConnectionId(deviceName, out string? connectionId))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // Pass isLowRes as false during PROPFIND so directory listing is instant (10ms) and uses 0% CPU on phone.
            // Low-res thumbnails will be generated on-demand during GET requests when visible on screen.
            var nodeInfo = await hubContext.Clients.Client(connectionId)
                .InvokeAsync<FileNodeResultDto>("GetPathInfo", relativePath, isLowRes, context.RequestAborted);

            if (nodeInfo == null || !nodeInfo.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            string requestPathBase = $"/communicate/{Uri.EscapeDataString(deviceName)}/{mode}";
            string xmlResponse = BuildWebDavXml(requestPathBase, nodeInfo.CurrentNode, nodeInfo.Children, isDepthZero);

            context.Response.StatusCode = 207; // Multi-Status
            context.Response.ContentType = "application/xml; charset=utf-8";
            await context.Response.WriteAsync(xmlResponse, Encoding.UTF8);
        }
        catch
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
            else
            {
                context.Abort();
            }
        }
    }

    private static async Task HandleGetAsync(
        HttpContext context, 
        IHubContext<CommunicateSignalR> hubContext, 
        ConnectionManager connectionManager, 
        string deviceName, 
        string relativePath, 
        bool isLowRes)
    {
        try
        {
            if (!connectionManager.TryGetConnectionId(deviceName, out string? connectionId))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var nodeInfo = await hubContext.Clients.Client(connectionId)
                .InvokeAsync<FileNodeResultDto>("GetPathInfo", relativePath, isLowRes, context.RequestAborted);

            if (nodeInfo == null || !nodeInfo.Exists || nodeInfo.CurrentNode.IsDirectory)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!ContentTypeProvider.TryGetContentType(nodeInfo.CurrentNode.Name, out string? contentType))
            {
                contentType = "application/octet-stream";
            }

            long totalLength = nodeInfo.CurrentNode.Length;

            long startOffset = 0;
            long endOffset = totalLength - 1;
            bool isRangeRequest = false;

            string? rangeHeader = context.Request.Headers["Range"].FirstOrDefault();
            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var rangeValues = rangeHeader["bytes=".Length..].Split('-');
                if (long.TryParse(rangeValues[0], out long parsedStart))
                {
                    startOffset = Math.Max(0, Math.Min(parsedStart, totalLength - 1));
                    isRangeRequest = true;
                }
                if (rangeValues.Length > 1 && long.TryParse(rangeValues[1], out long parsedEnd))
                {
                    endOffset = Math.Min(parsedEnd, totalLength - 1);
                    isRangeRequest = true;
                }
            }

            long contentLength = Math.Max(0, endOffset - startOffset + 1);

            if (isRangeRequest)
            {
                context.Response.StatusCode = StatusCodes.Status206PartialContent;
                context.Response.Headers["Content-Range"] = $"bytes {startOffset}-{endOffset}/{totalLength}";
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
            }

            context.Response.ContentType = contentType;
            context.Response.ContentLength = contentLength;

            if (context.Request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            long offset = startOffset;
            const int maxChunkSize = 512 * 1024; // 512 KB chunks (or 1024 * 1024 for 1 MB)

            while (offset <= endOffset)
            {
                context.RequestAborted.ThrowIfCancellationRequested();

                if (!connectionManager.TryGetConnectionId(deviceName, out string? activeConnectionId))
                {
                    throw new IOException($"Device '{deviceName}' disconnected during file transfer.");
                }

                int bytesToRead = (int)Math.Min(maxChunkSize, endOffset - offset + 1);

                byte[]? chunk = null;
                try
                {
                    chunk = await hubContext.Clients.Client(activeConnectionId)
                        .InvokeAsync<byte[]>("GetFileChunk", relativePath, offset, bytesToRead, isLowRes, context.RequestAborted);
                }
                catch (Exception ex)
                {
                    throw new IOException($"Failed to pull chunk at offset {offset} from device '{deviceName}'.", ex);
                }

                if (chunk == null || chunk.Length == 0)
                {
                    throw new IOException($"Unexpected end of stream at offset {offset} of {totalLength} bytes.");
                }

                await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
                offset += chunk.Length;
            }
        }
        catch (Exception)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
            else
            {
                context.Abort();
            }
        }
    }

    private static string BuildWebDavXml(string requestPathBase, FileNodeDto current, List<FileNodeDto>? children, bool isDepthZero)
    {
        XNamespace dav = "DAV:";
        var multiStatus = new XElement(dav + "multistatus");

        multiStatus.Add(CreateResponseElement(dav, requestPathBase, current));

        if (!isDepthZero && current.IsDirectory && children != null)
        {
            foreach (var child in children)
            {
                multiStatus.Add(CreateResponseElement(dav, requestPathBase, child));
            }
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), multiStatus);
        return doc.ToString();
    }

    private static XElement CreateResponseElement(XNamespace dav, string requestPathBase, FileNodeDto node)
    {
        string pathSuffix = string.IsNullOrEmpty(node.RelativePath) 
            ? "" 
            : "/" + string.Join('/', node.RelativePath.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

        string href = requestPathBase + pathSuffix;

        if (node.IsDirectory && !href.EndsWith("/"))
        {
            href += "/";
        }

        var prop = new XElement(dav + "prop",
            new XElement(dav + "displayname", node.Name),
            new XElement(dav + "getlastmodified", node.LastWriteTimeUtc.ToString("R"))
        );

        if (node.IsDirectory)
        {
            prop.Add(new XElement(dav + "resourcetype", new XElement(dav + "collection")));
        }
        else
        {
            prop.Add(new XElement(dav + "resourcetype"));
            prop.Add(new XElement(dav + "getcontentlength", node.Length));

            if (!ContentTypeProvider.TryGetContentType(node.Name, out string? contentType))
            {
                contentType = "application/octet-stream";
            }
            prop.Add(new XElement(dav + "getcontenttype", contentType));
        }

        return new XElement(dav + "response",
            new XElement(dav + "href", href),
            new XElement(dav + "propstat",
                prop,
                new XElement(dav + "status", "HTTP/1.1 200 OK")
            )
        );
    }
}