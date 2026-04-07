using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace HttpProxyLab
{
    class Program
    {
        private static readonly int ProxyPort = 8888;
        private static readonly string ProxyIP = "127.0.0.2";
        private static HashSet<string> Blacklist = new HashSet<string>();
        private static readonly object ConsoleLock = new object();

        static void Main(string[] args)
        {
            Console.WriteLine($"Прокси-сервер запущен на {ProxyIP}:{ProxyPort}");

            string blacklistPath = "blacklist.txt";
            if (File.Exists(blacklistPath))
            {
                foreach (var line in File.ReadAllLines(blacklistPath))
                {
                    if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
                    {
                        Blacklist.Add(line.Trim().ToLower());
                    }
                }
                Console.WriteLine($"Загружено {Blacklist.Count} записей в чёрный список");
            }

            TcpListener listener = new TcpListener(IPAddress.Parse(ProxyIP), ProxyPort);
            listener.Start();
            Console.WriteLine("Ожидание подключений...\n");

            while (true)
            {
                TcpClient client = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(HandleClient, client);
            }
        }

        static void HandleClient(object obj)
        {
            TcpClient client = (TcpClient)obj;

            try
            {
                NetworkStream clientStream = client.GetStream();
                byte[] buffer = new byte[8192];
                int bytesRead = clientStream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) return;

                string request = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                string firstLine = request.Split('\n')[0].Trim();
                string[] parts = firstLine.Split(' ');

                if (parts.Length < 2) return;

                string method = parts[0];
                string url = parts[1];

                if (method == "CONNECT") return;

                // Проверка чёрного списка
                foreach (string blocked in Blacklist)
                {
                    if (url.ToLower().Contains(blocked))
                    {
                        SendBlockedPage(clientStream, url);
                        lock (ConsoleLock)
                        {
                            Console.WriteLine(url + " - 403 Forbidden");
                        }
                        return;
                    }
                }

                // Получаем хост
                Match hostMatch = Regex.Match(request, @"Host:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
                if (!hostMatch.Success) return;

                string host = hostMatch.Groups[1].Value;
                int port = 80;
                if (host.Contains(":"))
                {
                    var hostParts = host.Split(':');
                    host = hostParts[0];
                    port = int.Parse(hostParts[1]);
                }

                // Получаем путь
                string path = url;
                if (url.StartsWith("http://"))
                {
                    try
                    {
                        Uri uri = new Uri(url);
                        path = uri.PathAndQuery;
                    }
                    catch { }
                }

                // Модифицируем запрос
                string newRequest = request.Replace(url, path);

                // Подключаемся к серверу
                using (TcpClient target = new TcpClient())
                {
                    target.Connect(host, port);
                    NetworkStream targetStream = target.GetStream();

                    byte[] requestBytes = Encoding.ASCII.GetBytes(newRequest);
                    targetStream.Write(requestBytes, 0, requestBytes.Length);

                    byte[] responseBuffer = new byte[65536];
                    bool statusLogged = false;
                    int statusCode = 0;

                    while (true)
                    {
                        int received = targetStream.Read(responseBuffer, 0, responseBuffer.Length);
                        if (received == 0) break;

                        if (!statusLogged)
                        {
                            string responseText = Encoding.ASCII.GetString(responseBuffer, 0, Math.Min(received, 500));

                            // Ищем всю строку статуса: "HTTP/1.1 200 OK"
                            Match statusMatch = Regex.Match(responseText, @"HTTP/\d\.\d\s+(\d+)\s+(.+?)(?:\r|\n)");
                            if (statusMatch.Success)
                            {
                                statusCode = int.Parse(statusMatch.Groups[1].Value);
                                string statusMessage = statusMatch.Groups[2].Value;  

                                lock (ConsoleLock)
                                {
                                    Console.WriteLine(url + " - " + statusCode + " " + statusMessage);
                                }
                                statusLogged = true;
                            }
                        }

                        clientStream.Write(responseBuffer, 0, received);
                        clientStream.Flush();
                    }
                }
            }
            catch (Exception)
            {
                // Игнорируем ошибки
            }
            finally
            {
                client.Close();
            }
        }

        static void SendBlockedPage(NetworkStream stream, string url)
        {
            string html = "<html><head><meta charset='UTF-8'></head>" +
                          "<body><h1>403 Forbidden</h1>" +
                          "<p>Доступ к " + url + " заблокирован</p></body></html>";

            byte[] htmlBytes = Encoding.UTF8.GetBytes(html);

            string response = $"HTTP/1.1 403 Forbidden\r\n" +
                             $"Content-Type: text/html; charset=utf-8\r\n" +
                             $"Content-Length: {htmlBytes.Length}\r\n" +
                             $"Connection: close\r\n\r\n";

            byte[] responseBytes = Encoding.ASCII.GetBytes(response);
            stream.Write(responseBytes, 0, responseBytes.Length);
            stream.Write(htmlBytes, 0, htmlBytes.Length);
            stream.Flush();
        }
    }
}

// "C:\Program Files\Google\Chrome\Application\chrome.exe" --proxy-server=http://127.0.0.2:8888