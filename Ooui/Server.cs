using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.WebSockets;

namespace Ooui
{
    public class Server
    {
        readonly Dictionary<string, Func<Element>> publishedPaths =
            new Dictionary<string, Func<Element>> ();

        readonly static byte[] clientJsBytes;

        static Server ()
        {
            var asm = typeof(Server).Assembly;
            System.Console.WriteLine("ASM = {0}", asm);
            foreach (var n in asm.GetManifestResourceNames()) {
                System.Console.WriteLine("  {0}", n);
            }
            using (var s = asm.GetManifestResourceStream ("Ooui.Client.js")) {
                using (var r = new StreamReader (s)) {
                    clientJsBytes = Encoding.UTF8.GetBytes (r.ReadToEnd ());
                }
            }
        }

        public Task RunAsync (string listenerPrefix)
        {
            return RunAsync (listenerPrefix, CancellationToken.None);
        }

        public async Task RunAsync (string listenerPrefix, CancellationToken token)
        {
            var listener = new HttpListener ();
            listener.Prefixes.Add (listenerPrefix);
            listener.Start ();
            Console.WriteLine ($"Listening at {listenerPrefix}...");

            while (!token.IsCancellationRequested) {
                var listenerContext = await listener.GetContextAsync ().ConfigureAwait (false);
                if (listenerContext.Request.IsWebSocketRequest) {
                    ProcessWebSocketRequest (listenerContext, token);
                }
                else {
                    ProcessRequest (listenerContext, token);
                }
            }
        }

        public void Publish (string path, Func<Element> elementCtor)
        {
            System.Console.WriteLine($"PUBLISH {path}");
            publishedPaths[path] = elementCtor;
        }

        public void Publish (string path, Element element)
        {
            Publish (path, () => element);
        }

        void ProcessRequest (HttpListenerContext listenerContext, CancellationToken token)
        {
            var url = listenerContext.Request.Url;
            var path = url.LocalPath;

            Console.WriteLine ($"{listenerContext.Request.HttpMethod} {url.LocalPath}");

            var response = listenerContext.Response;

            Func<Element> ctor;

            if (path == "/client.js") {
                response.ContentLength64 = clientJsBytes.LongLength;
                response.ContentType = "application/javascript";
                response.ContentEncoding = Encoding.UTF8;
                using (var s = response.OutputStream) {
                    s.Write (clientJsBytes, 0, clientJsBytes.Length);
                }
            }
            else if (publishedPaths.TryGetValue (path, out ctor)) {
                var element = ctor ();
                RegisterElement (element);
                WriteElementHtml (element, response);
            }
            else {
                response.StatusCode = 404;
                response.Close ();
            }
        }

        void RegisterElement (Element element)
        {
        }

        void WriteElementHtml (Element element, HttpListenerResponse response)
        {
            response.StatusCode = 200;
            response.ContentType = "text/html";
            response.ContentEncoding = Encoding.UTF8;
            using (var s = response.OutputStream) {
                using (var w = new StreamWriter (s, Encoding.UTF8)) {
                    w.WriteLine ($"<html><head>");
                    w.WriteLine ($"<title>{element}</title>");
                    w.WriteLine ($"</head><body>");
                    w.WriteLine ($"<script src=\"/client.js\"> </script>");
                    w.WriteLine ($"<body></html>");
                }
            }
            response.Close ();
        }

        async void ProcessWebSocketRequest (HttpListenerContext listenerContext, CancellationToken token)
        {
            WebSocketContext webSocketContext = null;
            try {
                webSocketContext = await listenerContext.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait (false);
                Console.WriteLine ("Accepted WebSocket: {0}", webSocketContext);
            }
            catch (Exception e) {
                listenerContext.Response.StatusCode = 500;
                listenerContext.Response.Close();
                Console.WriteLine ("Failed to accept WebSocket: {0}", e);
                return;
            }

            WebSocket webSocket = null;

            try {
                webSocket = webSocketContext.WebSocket;

                var receiveBuffer = new byte[1024];

                while (webSocket.State == WebSocketState.Open && !token.IsCancellationRequested) {
                    var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), token);

                    if (receiveResult.MessageType == WebSocketMessageType.Close) {
                        await webSocket.CloseAsync (WebSocketCloseStatus.NormalClosure, "", token).ConfigureAwait (false);
                    }
                    else if (receiveResult.MessageType == WebSocketMessageType.Binary) {
                        await webSocket.CloseAsync (WebSocketCloseStatus.InvalidMessageType, "Cannot accept binary frame", token).ConfigureAwait (false);
                    }
                    else {
                        var size = receiveResult.Count;
                        while (!receiveResult.EndOfMessage) {
                            if (size >= receiveBuffer.Length) {
                                await webSocket.CloseAsync (WebSocketCloseStatus.MessageTooBig, "Message too big", token).ConfigureAwait (false);
                                return;
                            }
                            receiveResult = await webSocket.ReceiveAsync (new ArraySegment<byte>(receiveBuffer, size, receiveBuffer.Length - size), token).ConfigureAwait (false);
                            size += receiveResult.Count;
                        }
                        var receivedString = Encoding.UTF8.GetString (receiveBuffer, 0, size);
                        Console.WriteLine ("RECEIVED: {0}", receivedString);

                        var outputBuffer = new ArraySegment<byte> (Encoding.UTF8.GetBytes ($"You said: {receivedString}"));
                        await webSocket.SendAsync (outputBuffer, WebSocketMessageType.Text, true, token).ConfigureAwait (false);
                    }
                }
            }
            catch (Exception e) {
                Console.WriteLine ("Exception: {0}", e);
            }
            finally {
                webSocket?.Dispose();
            }
        }
    }
}
