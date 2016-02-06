// Copyright (c) Microsoft. All rights reserved.


using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Http;
using Windows.Foundation.Collections;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.AppService;
using Windows.System.Threading;
using Windows.Networking.Sockets;
using System.IO;
using Windows.Storage.Streams;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using System.Collections.Specialized;
using System.Text.RegularExpressions;
using System.Net;

namespace WebServerTask
{
    public sealed class WebServerBGTask : IBackgroundTask
    {
        public void Run(IBackgroundTaskInstance taskInstance)
        {
            // Associate a cancellation handler with the background task. 
            taskInstance.Canceled += OnCanceled;

            // Get the deferral object from the task instance
            serviceDeferral = taskInstance.GetDeferral();

            var appService = taskInstance.TriggerDetails as AppServiceTriggerDetails;
            if (appService != null &&
                appService.Name == "App2AppComService")
            {
                appServiceConnection = appService.AppServiceConnection;
                appServiceConnection.RequestReceived += OnRequestReceived;
            }
        }

        private async void OnRequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
        {
            var message = args.Request.Message;
            string command = message["Command"] as string;

            switch (command)
            {
                case "Initialize":
                    {
                        var messageDeferral = args.GetDeferral();
                        //Set a result to return to the caller
                        var returnMessage = new ValueSet();
                        server = new HttpServer(8000, appServiceConnection);
                        IAsyncAction asyncAction = Windows.System.Threading.ThreadPool.RunAsync(
                            (workItem) =>
                            {
                                server.StartServer();
                            });
                        returnMessage.Add("Status", "Success");
                        var responseStatus = await args.Request.SendResponseAsync(returnMessage);
                        messageDeferral.Complete();
                        break;
                    }

                case "Quit":
                    {
                        //Service was asked to quit. Give us service deferral
                        //so platform can terminate the background task
                        serviceDeferral.Complete();
                        break;
                    }
            }
        }
        private void OnCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            //Clean up and get ready to exit
        }

        HttpServer server;
        BackgroundTaskDeferral serviceDeferral;
        AppServiceConnection appServiceConnection;
    }

    public sealed class HttpServer : IDisposable
    {
        string htmlString = "<html><head><title>Scrolling Text App</title></head><body><form action =\"text.html\" method=\"GET\"><input type=\"text\" name=\"message\">Message to send<br><input type=\"text\" name=\"repeat\">Times to repeat<input type=\"submit\" value=\"Submit\" onclick=\"this.form.submit()\"></form></body></html>";
        private const uint BufferSize = 8192;
        private int port = 8000;
        private readonly StreamSocketListener listener;
        private AppServiceConnection appServiceConnection;

        public HttpServer(int serverPort, AppServiceConnection connection)
        {
            listener = new StreamSocketListener();
            port = serverPort;
            appServiceConnection = connection;
            listener.ConnectionReceived += (s, e) => ProcessRequestAsync(e.Socket);
        }

        public void StartServer()
        {
#pragma warning disable CS4014
            listener.BindServiceNameAsync(port.ToString());
#pragma warning restore CS4014
        }

        public void Dispose()
        {
            listener.Dispose();
        }

        private async void ProcessRequestAsync(StreamSocket socket)
        {
            // this works for text only
            StringBuilder request = new StringBuilder();
            using (IInputStream input = socket.InputStream)
            {
                byte[] data = new byte[BufferSize];
                IBuffer buffer = data.AsBuffer();
                uint dataRead = BufferSize;
                while (dataRead == BufferSize)
                {
                    await input.ReadAsync(buffer, BufferSize, InputStreamOptions.Partial);
                    request.Append(Encoding.UTF8.GetString(data, 0, data.Length));
                    dataRead = buffer.Length;
                }
            }

            using (IOutputStream output = socket.OutputStream)
            {
                string requestMethod = request.ToString().Split('\n')[0];
                string[] requestParts = requestMethod.Split(' ');

                if (requestParts[0] == "GET")
                    await WriteResponseAsync(requestParts[1], output);
                else
                    throw new InvalidDataException("HTTP method not supported: "
                                                   + requestParts[0]);
            }
        }

        private async Task WriteResponseAsync(string request, IOutputStream os)
        {
            // See if the request is for text.html, if yes get the new state
            string message = "Unspecified";
            string repeat = "0"; // we'll convert this to an int in the client side
            bool stateChanged = false;
            if (request.Contains("text.html?message="))
            {

                // sadly we don't have HttpUtility class
                // this is so wrong
                var stringUrl = "http://localhost" + request; // I have to spoof the url
             
                var parameters = UriExtensions.ParseQueryString(new Uri(stringUrl));
                message = parameters["message"];
                message = Uri.UnescapeDataString(message);
                message = message.Replace("+", " ");

                repeat = parameters["repeat"];
                stateChanged = true;
            }

            if (stateChanged)
            {
                var updateMessage = new ValueSet();
                updateMessage.Add("message", message);
                updateMessage.Add("repeat", repeat);
                var responseStatus = await appServiceConnection.SendMessageAsync(updateMessage);
            }

            // Show the html 
            using (Stream resp = os.AsStreamForWrite())
            {
                // Look in the Data subdirectory of the app package
                byte[] bodyArray = Encoding.UTF8.GetBytes(htmlString);
                MemoryStream stream = new MemoryStream(bodyArray);
                string header = String.Format("HTTP/1.1 200 OK\r\n" +
                                  "Content-Length: {0}\r\n" +
                                  "Connection: close\r\n\r\n",
                                  stream.Length);
                byte[] headerArray = Encoding.UTF8.GetBytes(header);
                await resp.WriteAsync(headerArray, 0, headerArray.Length);
                await stream.CopyToAsync(resp);
                await resp.FlushAsync();
            }

        }
    }

    public static class UriExtensions
    {
        private static readonly Regex _regex = new Regex(@"[?|&]([\w\.]+)=([^?|^&]+)");

        public static IReadOnlyDictionary<string, string> ParseQueryString(this Uri uri)
        {
            var match = _regex.Match(uri.PathAndQuery);
            var paramaters = new Dictionary<string, string>();
            while (match.Success)
            {
                paramaters.Add(match.Groups[1].Value, match.Groups[2].Value);
                match = match.NextMatch();
            }
            return paramaters;
        }
    }
}
