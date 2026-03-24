using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Linq;
using System.IO;

namespace P2PChat
{
    public class ChatNode
    {
        private readonly IPAddress _localIp;
        private readonly int _tcpPort;
        private readonly int _udpPort;
        private readonly string _userName;
        private readonly string _historyFilePath;

        private TcpListener _tcpListener;
        private UdpClient _udpBroadcastSender;
        private UdpClient _udpListener;
        private readonly ConcurrentDictionary<string, TcpClient> _connectedClients;
        private readonly ConcurrentDictionary<string, NodeInfo> _activeNodes;
        private readonly List<HistoryEntry> _history;
        private readonly object _historyLock = new object();
        private readonly object _fileLock = new object();

        private CancellationTokenSource _cts;
        private bool _isRunning;

        public ChatNode(IPAddress localIp, int tcpPort, int udpPort, string userName)
        {
            _localIp = localIp;
            _tcpPort = tcpPort;
            _udpPort = udpPort;
            _userName = userName;
            _connectedClients = new ConcurrentDictionary<string, TcpClient>();
            _activeNodes = new ConcurrentDictionary<string, NodeInfo>();
            _history = new List<HistoryEntry>();
            _cts = new CancellationTokenSource();

            string fileName = $"chat_history_{userName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            _historyFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
        }

        public async Task StartAsync()
        {
            _isRunning = true;

            Console.Clear();
            Console.WriteLine($"=== P2P Chat Node: {_userName} ===");
            Console.WriteLine($"Local IP: {_localIp}");
            Console.WriteLine($"TCP Port: {_tcpPort}");
            Console.WriteLine($"UDP Port: {_udpPort}");
            Console.WriteLine($"History file: {_historyFilePath}");
            Console.WriteLine(new string('=', 50));

            WriteToFile($"[SESSION START] Chat session started by {_userName} at {_localIp}:{_tcpPort}");
            AddHistoryEntry(new HistoryEntry
            {
                Timestamp = DateTime.Now,
                Type = "session_start",
                SenderName = _userName,
                SenderIp = _localIp.ToString(),
                Content = $"Chat session started by {_userName} ({_localIp})"
            });

            // TCP сервер
            try
            {
                _tcpListener = new TcpListener(_localIp, _tcpPort);
                _tcpListener.Start();
                Console.WriteLine($"[OK] TCP server listening on {_localIp}:{_tcpPort}");
                _ = Task.Run(() => AcceptTcpConnectionsAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to start TCP server: {ex.Message}");
                return;
            }

            // UDP слушатель
            try
            {
                _udpListener = new UdpClient();
                _udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpListener.Client.Bind(new IPEndPoint(_localIp, _udpPort));
                Console.WriteLine($"[OK] UDP listener bound to {_localIp}:{_udpPort}");
                _ = Task.Run(() => ListenForBroadcastsAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to start UDP listener: {ex.Message}");
            }

            _udpBroadcastSender = new UdpClient();
            _udpBroadcastSender.EnableBroadcast = true;

            // Отправляем broadcast
            await Task.Delay(1000);
            await SendBroadcastAsync(MessageType.UserConnected);

            _ = Task.Run(() => HandleUserInputAsync());
            _ = Task.Run(() => PingNodesAsync(_cts.Token));

            Console.WriteLine("\nChat started!");
            Console.WriteLine("Commands:");
            Console.WriteLine("  /quit - exit");
            Console.WriteLine("  /nodes - show active nodes");
            Console.WriteLine("  /history - show message history");
            //Console.WriteLine("  /connect <ip> <port> - manually connect to a node");
            Console.WriteLine(new string('-', 50));
            

            try
            {
                await Task.Delay(Timeout.Infinite, _cts.Token);
            }
            catch (TaskCanceledException) { }
        }

        private async Task AcceptTcpConnectionsAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var tcpClient = await _tcpListener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleTcpClientAsync(tcpClient, token));
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    Console.WriteLine($"[ERROR] Accepting connection: {ex.Message}");
                }
            }
        }


        private async Task HandleTcpClientAsync(TcpClient client, CancellationToken token)
        {
            string nodeKey = null;      // будет хранить "IP:порт" другого узла
            string remoteName = null;   // будет хранить имя другого узла
            NetworkStream stream = null; // поток для чтения/записи данных

            try
            {
                stream = client.GetStream();
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;

                // Отправляем свое имя
                var nameMessage = new ChatMessage
                {
                    Type = MessageType.NameExchange,
                    Content = _userName,
                    SenderName = _userName,
                    SenderIp = _localIp.ToString(),
                    SenderTcpPort = _tcpPort
                };
                await SendMessageAsync(stream, nameMessage, token);

                // Получаем имя от удаленного узла
                var buffer = new byte[4096];
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);

                if (bytesRead > 0)
                {
                    var message = ChatMessage.Deserialize(buffer.Take(bytesRead).ToArray());
                    if (message != null && message.Type == MessageType.NameExchange)
                    {
                        remoteName = message.Content;
                        int remoteTcpPort = message.SenderTcpPort;
                        string remoteIp = message.SenderIp;
                        nodeKey = $"{remoteIp}:{remoteTcpPort}";

                        // Проверка на дублирование
                        if (_activeNodes.ContainsKey(nodeKey))
                        {
                           // Console.WriteLine($"[DEBUG] Already connected to {remoteName}, closing");
                            return;
                        }

                        // Сохраняем соединение
                        _connectedClients[nodeKey] = client;
                        _activeNodes[nodeKey] = new NodeInfo
                        {
                            Name = remoteName,
                            Ip = remoteIp,
                            Port = remoteTcpPort,
                            LastSeen = DateTime.Now,
                            Client = client
                        };

                       
                        Console.WriteLine($"\n*** {remoteName} ({remoteIp}:{remoteTcpPort}) joined the chat ***\n");
                       

                        var tcs = new TaskCompletionSource<bool>();

                        // Запускаем слушатель в фоне
                        _ = Task.Run(() => ListenForMessagesAsync(client, stream, nodeKey, remoteName, token, tcs));
                       // Console.WriteLine($"[DEBUG] ListenForMessagesAsync LAUNCHED for {remoteName}");

                        // Ждем пока слушатель запустится (максимум 2 секунды)
                        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

                        // Отправляем историю
                        await SendHistoryAsync(stream, token);

                        // Запрашиваем историю
                        await RequestHistoryAsync(stream, token);
                    }
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Console.WriteLine($"[DEBUG] Error in HandleTcpClientAsync: {ex.Message}");
            }
        }

        private async Task ListenForMessagesAsync(TcpClient client, NetworkStream stream, string nodeKey, string remoteName, CancellationToken token, TaskCompletionSource<bool> tcs = null)
        {
            
            tcs?.TrySetResult(true);

            var buffer = new byte[65536];      // буфер для чтения из сокета (64 КБ)
            var receiveBuffer = new List<byte>(); // буфер для накопления данных

            try
            {
                while (!token.IsCancellationRequested && client.Connected)
                {
                    try
                    {
                        int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);

                        if (bytesRead > 0)
                        {
                          //  Console.WriteLine($"[DEBUG] Received {bytesRead} bytes from {remoteName}");

                            receiveBuffer.AddRange(buffer.Take(bytesRead));

                            int offset = 0;
                            while (offset + 3 <= receiveBuffer.Count)
                            {
                                byte typeByte = receiveBuffer[offset];
                                int messageLength = (receiveBuffer[offset + 1] << 8) | receiveBuffer[offset + 2];

                              //  Console.WriteLine($"[DEBUG] Message header: type={typeByte}, length={messageLength}");

                                if (offset + 3 + messageLength <= receiveBuffer.Count)
                                {
                                    var messageData = receiveBuffer.Skip(offset + 3).Take(messageLength).ToArray();
                                    var json = Encoding.UTF8.GetString(messageData);

                                  //  Console.WriteLine($"[DEBUG] Extracted message, JSON length: {messageLength}");

                                    var message = JsonSerializer.Deserialize<ChatMessage>(json);
                                    if (message != null)
                                    {
                                        message.Type = (MessageType)typeByte;
                                      //  Console.WriteLine($"[DEBUG] Successfully deserialized message type: {message.Type}");

                                        await ProcessMessageAsync(message, stream, nodeKey, remoteName, token);
                                    }
                                    else
                                    {
                                       // Console.WriteLine($"[DEBUG] Failed to deserialize message from {remoteName}");
                                    }

                                    offset += 3 + messageLength;
                                }
                                else
                                {
                                    Console.WriteLine($"[DEBUG] Not enough data for full message, waiting for more. Need {3 + messageLength}, have {receiveBuffer.Count - offset}");
                                    break;
                                }
                            }

                            if (offset > 0)
                            {
                                receiveBuffer.RemoveRange(0, offset);
                                //Console.WriteLine($"[DEBUG] Removed {offset} bytes from buffer, remaining: {receiveBuffer.Count}");
                            }
                        }
                        else
                        {
                           // Console.WriteLine($"[DEBUG] Connection closed by {remoteName} (bytesRead = 0)");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                       // Console.WriteLine($"[DEBUG] Exception in read loop for {remoteName}: {ex.Message}");
                        break;
                    }
                }
            }
            finally
            {
              //  Console.WriteLine($"[DEBUG] ========== ListenForMessagesAsync CLEANUP for {remoteName} ==========");

                if (_activeNodes.TryGetValue(nodeKey, out var disconnectedNode))
                {
                    _activeNodes.TryRemove(nodeKey, out _);
                    _connectedClients.TryRemove(nodeKey, out _);

                    
                    AddHistoryEntry(new HistoryEntry
                    {
                        Timestamp = DateTime.Now,
                        Type = "session_end",
                        SenderName = disconnectedNode.Name,
                        SenderIp = disconnectedNode.Ip,
                        Content = $"{disconnectedNode.Name} ({disconnectedNode.Ip}) left the chat"
                    });

                   
                    Console.WriteLine($"\n*** {disconnectedNode.Name} ({disconnectedNode.Ip}:{disconnectedNode.Port}) left the chat ***\n");
                    
                }

                try
                {
                    client?.Close();
                }
                catch { }

              //  Console.WriteLine($"[DEBUG] ========== ListenForMessagesAsync FINISHED for {remoteName} ==========");
            }
        }

        private async Task ProcessMessageAsync(ChatMessage message, NetworkStream stream, string nodeKey, string remoteName, CancellationToken token)
        {
            switch (message.Type)
            {
                case MessageType.TextMessage:

                    // добавляем в историю
                    AddHistoryEntry(new HistoryEntry
                    {
                        Timestamp = DateTime.Now,
                        Type = "message",
                        SenderName = remoteName,
                        SenderIp = message.SenderIp,
                        Content = message.Content
                    });

                    Console.WriteLine($"\n[{remoteName}]: {message.Content}\n");
                    
                    break;

                case MessageType.HistoryResponse:;
                    await ReceiveHistoryAsync(message.Content);
                    break;

                case MessageType.HistoryRequest:
                    await SendHistoryAsync(stream, token);
                    break;

                case MessageType.Ping:
                    var pongMessage = new ChatMessage
                    {
                        Type = MessageType.Pong,
                        SenderName = _userName,
                        SenderIp = _localIp.ToString(),
                        SenderTcpPort = _tcpPort
                    };
                    await SendMessageAsync(stream, pongMessage, token);
                    break;

                case MessageType.Pong:
                    if (_activeNodes.TryGetValue(nodeKey, out var node))
                    {
                        node.LastSeen = DateTime.Now;
                    }
                    break;

                case MessageType.NameExchange:
                    break;

                default:
                    break;
            }
        }

        private async Task ListenForBroadcastsAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpListener.ReceiveAsync();
                    var message = ChatMessage.Deserialize(result.Buffer);

                    if (message != null && message.SenderTcpPort != _tcpPort)
                    {
                        string senderIp = message.SenderIp;
                        string nodeKey = $"{senderIp}:{message.SenderTcpPort}";

                        if (!_activeNodes.ContainsKey(nodeKey))
                        {
                            
                            await ConnectToNodeAsync(senderIp, message.SenderTcpPort);
                            
                        }
                        else
                        {
                        }
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    // Игнорируем ошибки
                }
            }
        }

        private async Task ConnectToNodeAsync(string ip, int port)
        {
            string nodeKey = $"{ip}:{port}";

            if (_activeNodes.ContainsKey(nodeKey))
            {
                return;
            }

            if (ip == _localIp.ToString() && port == _tcpPort)
                return;

            try
            {
                var client = new TcpClient();
                await client.ConnectAsync(ip, port);

                var stream = client.GetStream();

                var nameMessage = new ChatMessage
                {
                    Type = MessageType.NameExchange,
                    Content = _userName,
                    SenderName = _userName,
                    SenderIp = _localIp.ToString(),
                    SenderTcpPort = _tcpPort
                };

                await SendMessageAsync(stream, nameMessage, CancellationToken.None);

                _connectedClients[nodeKey] = client;
                _ = Task.Run(() => HandleTcpClientAsync(client, CancellationToken.None));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] Failed to connect to {ip}:{port}: {ex.Message}");
            }
        }

        private async Task SendBroadcastAsync(MessageType type)
        {
            try
            {
                var message = new ChatMessage
                {
                    Type = type,
                    Content = _userName,
                    SenderName = _userName,
                    SenderIp = _localIp.ToString(),
                    SenderTcpPort = _tcpPort
                };

                var data = message.Serialize();

                using (var udpClient = new UdpClient())
                {
                    udpClient.EnableBroadcast = true;
                    udpClient.Client.Bind(new IPEndPoint(_localIp, 0));

                    byte[] ipBytes = _localIp.GetAddressBytes();
                    ipBytes[3] = 255;
                    var broadcastAddress = new IPAddress(ipBytes);
                    var broadcastEndpoint = new IPEndPoint(broadcastAddress, _udpPort);

                    await udpClient.SendAsync(data, data.Length, broadcastEndpoint);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] Broadcast error: {ex.Message}");
            }
        }

        private async Task SendMessageToAllAsync(string text)
        {
            var message = new ChatMessage
            {
                Type = MessageType.TextMessage,
                Content = text,
                SenderName = _userName,
                SenderIp = _localIp.ToString(),
                SenderTcpPort = _tcpPort
            };

            AddHistoryEntry(new HistoryEntry
            {
                Timestamp = DateTime.Now,
                Type = "self",
                SenderName = _userName,
                SenderIp = _localIp.ToString(),
                Content = text
            });

            Console.WriteLine($"[You]: {text}");
            

            var data = message.Serialize();
            var failedKeys = new List<string>();

            foreach (var (nodeKey, client) in _connectedClients)
            {
                try
                {
                    if (client.Connected)
                    {
                        var stream = client.GetStream();
                        await stream.WriteAsync(data, 0, data.Length);
                        await stream.FlushAsync();
                    }
                    else
                    {
                        failedKeys.Add(nodeKey);
                    }
                }
                catch
                {
                    failedKeys.Add(nodeKey);
                }
            }

            foreach (var key in failedKeys)
            {
                _connectedClients.TryRemove(key, out _);
                _activeNodes.TryRemove(key, out _);
            }
        }
        private async Task SendHistoryAsync(NetworkStream stream, CancellationToken token)
        {
            lock (_historyLock)
            {
                var historyData = _history.Select(h => new
                {
                    h.Timestamp,
                    h.Type,
                    h.SenderName,
                    h.SenderIp,
                    h.Content
                }).ToList();

                var historyJson = JsonSerializer.Serialize(historyData);
                var historyMessage = new ChatMessage
                {
                    Type = MessageType.HistoryResponse,
                    Content = historyJson,
                    SenderName = _userName,
                    SenderIp = _localIp.ToString(),
                    SenderTcpPort = _tcpPort
                };

                SendMessageAsync(stream, historyMessage, token).Wait(1000);
            }
        }

        private async Task RequestHistoryAsync(NetworkStream stream, CancellationToken token)
        {
            var requestMessage = new ChatMessage
            {
                Type = MessageType.HistoryRequest,
                Content = "",
                SenderName = _userName,
                SenderIp = _localIp.ToString(),
                SenderTcpPort = _tcpPort
            };

            await SendMessageAsync(stream, requestMessage, token);
        }

        private async Task ReceiveHistoryAsync(string historyJson)
        {
            try
            {
                var historyEntries = JsonSerializer.Deserialize<List<dynamic>>(historyJson);
                if (historyEntries != null && historyEntries.Any())
                {
                    
                    int newEntriesCount = 0;

                    lock (_historyLock)
                    {
                        foreach (var entry in historyEntries)
                        {
                            var timestamp = entry.GetProperty("Timestamp").GetDateTime();
                            var type = entry.GetProperty("Type").GetString();
                            var content = entry.GetProperty("Content").GetString();
                            var senderName = entry.GetProperty("SenderName").GetString();
                            var senderIp = entry.GetProperty("SenderIp").GetString();

                            // Проверяем, нет ли уже такого сообщения в истории
                            bool exists = _history.Any(h =>
                                Math.Abs((h.Timestamp - timestamp).TotalSeconds) < 1 &&
                                h.Content == content);

                            if (!exists)
                            {
                                string actualType = type;

                                if (type == "self")
                                {
                                    actualType = "message";
                                }
                                

                                _history.Add(new HistoryEntry
                                {
                                    Timestamp = timestamp,
                                    Type = actualType,
                                    SenderName = senderName,
                                    SenderIp = senderIp,
                                    Content = content
                                });
                                newEntriesCount++;

                                
                                string fileText = actualType switch
                                {
                                    "message" => $"[{senderName} ({senderIp})]: {content}",
                                    "session_start" => content,
                                    "session_end" => content,
                                    _ => content
                                };
                                WriteToFile($"[{timestamp:yyyy-MM-dd HH:mm:ss}] {fileText}");
                            }
                        }

                        _history.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
                    }

                    
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] Error receiving history: {ex.Message}");
            }
        }

        private async Task SendMessageAsync(NetworkStream stream, ChatMessage message, CancellationToken token)
        {
            try
            {
                var data = message.Serialize();
                await stream.WriteAsync(data, 0, data.Length, token);
                await stream.FlushAsync(token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] Error sending message: {ex.Message}");
                throw;
            }
        }

        private async Task PingNodesAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(120000, token);

                foreach (var (nodeKey, client) in _connectedClients)
                {
                    try
                    {
                        if (client.Connected)
                        {
                            var pingMessage = new ChatMessage
                            {
                                Type = MessageType.Ping,
                                SenderName = _userName,
                                SenderIp = _localIp.ToString(),
                                SenderTcpPort = _tcpPort
                            };

                            var stream = client.GetStream();
                            await SendMessageAsync(stream, pingMessage, token);
                        }
                    }
                    catch { }
                }

                var staleNodes = _activeNodes.Where(n => (DateTime.Now - n.Value.LastSeen).TotalSeconds > 300).ToList();
                foreach (var stale in staleNodes)
                {
                    _activeNodes.TryRemove(stale.Key, out _);
                    _connectedClients.TryRemove(stale.Key, out _);
                }
            }
        }

        private async Task HandleUserInputAsync()
        {
            while (_isRunning)
            {
                var input = await Console.In.ReadLineAsync();

                if (input == null)
                    continue;

                if (input.Equals("/quit", StringComparison.OrdinalIgnoreCase))
                {
                    Stop();
                    break;
                }

                if (input.Equals("/nodes", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("\n=== Active Nodes ===");
                    if (_activeNodes.Count == 0)
                    {
                        Console.WriteLine("No active nodes");
                    }
                    else
                    {
                        foreach (var node in _activeNodes)
                        {
                            Console.WriteLine($"{node.Value.Name} at {node.Value.Ip}:{node.Value.Port}");
                        }
                        Console.WriteLine($"Total: {_activeNodes.Count} nodes");
                    }
                    
                    continue;
                }

                if (input.Equals("/history", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("\n=== Message History ===");
                    lock (_historyLock)
                    {
                        if (_history.Count == 0)
                        {
                            Console.WriteLine("No messages yet");
                        }
                        else
                        {
                            foreach (var entry in _history)
                            {
                                Console.WriteLine(entry.ToHistoryString());
                            }
                        }
                    }
                    
                    continue;
                }

                if (input.Equals("/sync", StringComparison.OrdinalIgnoreCase))
                {
                   // Console.WriteLine("[SYSTEM] Requesting history from all connected nodes...");
                    int requestedCount = 0;

                    foreach (var (nodeKey, client) in _connectedClients)
                    {
                        try
                        {
                            if (client.Connected)
                            {
                                var stream = client.GetStream();
                                await RequestHistoryAsync(stream, CancellationToken.None);
                               // Console.WriteLine($"[SYSTEM] History requested from {nodeKey}");
                                requestedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[SYSTEM] Failed to request history from {nodeKey}: {ex.Message}");
                        }
                    }

                    if (requestedCount == 0)
                    {
                        Console.WriteLine("[SYSTEM] No connected nodes to request history from");
                    }
                    else
                    {
                        Console.WriteLine($"[SYSTEM] History requested from {requestedCount} node(s)");
                    }

                    
                    continue;
                }

                if (input.StartsWith("/connect", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = input.Split(' ');
                    if (parts.Length >= 3 && int.TryParse(parts[2], out int port))
                    {
                        string ip = parts[1];
                        //Console.WriteLine($"[SYSTEM] Manually connecting to {ip}:{port}");
                        await ConnectToNodeAsync(ip, port);
                    }
                    else
                    {
                        Console.WriteLine("[SYSTEM] Usage: /connect <ip> <port>");
                    }
                    
                    continue;
                }

                if (string.IsNullOrWhiteSpace(input))
                {
                    
                    continue;
                }

                await SendMessageToAllAsync(input);
            }
        }

        private void AddHistoryEntry(HistoryEntry entry)
        {
            lock (_historyLock)
            {
                
                _history.Add(entry);
                WriteToFile(entry.ToFileString());
            }
        }

        private void WriteToFile(string message)
        {
            lock (_fileLock)
            {
                try
                {
                    File.AppendAllText(_historyFilePath, message + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed to write to history file: {ex.Message}");
                }
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _cts.Cancel();

            Console.WriteLine("\nShutting down...");

            try
            {
                SendBroadcastAsync(MessageType.UserDisconnected).Wait(1000);
            }
            catch { }

            foreach (var client in _connectedClients.Values)
            {
                try { client?.Close(); } catch { }
            }

            try
            {
                _tcpListener?.Stop();
                _udpListener?.Close();
                _udpBroadcastSender?.Close();
            }
            catch { }

            AddHistoryEntry(new HistoryEntry
            {
                Timestamp = DateTime.Now,
                Type = "session_end",
                SenderName = _userName,
                SenderIp = _localIp.ToString(),
                Content = $"{_userName} ({_localIp}) left the chat"
            });

            // Записываем в файл
            WriteToFile($"[SESSION END] Chat session ended at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            WriteToFile(new string('=', 60));

            Console.WriteLine($"Chat stopped. History saved to: {_historyFilePath}");
        }

    }
}