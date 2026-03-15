//"D:\Programist\ksis\mytracert\bin\Debug\net8.0"
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class MyTracert
{
    class IcmpHeader
    {
        public byte Type { get; set; }
        public byte Code { get; set; }
        public ushort Checksum { get; set; }
        public ushort Identifier { get; set; }
        public ushort SequenceNumber { get; set; }
        public byte[] Data { get; set; }

        // преобразуем структуру ICMP-заголовка в массив байт
        public byte[] GetBytes()
        {
            var buffer = new byte[8 + (Data?.Length ?? 0)];
            buffer[0] = Type;
            buffer[1] = Code;
            buffer[2] = (byte)(Checksum >> 8);
            buffer[3] = (byte)(Checksum & 0xFF);
            buffer[4] = (byte)(Identifier >> 8);
            buffer[5] = (byte)(Identifier & 0xFF);
            buffer[6] = (byte)(SequenceNumber >> 8);
            buffer[7] = (byte)(SequenceNumber & 0xFF);

            if (Data != null)
                Array.Copy(Data, 0, buffer, 8, Data.Length);

            return buffer;
        }
    }

    static ushort CalculateChecksum(byte[] data)
    {
        long sum = 0; // 64 бита чтобы избежать переполнения
        for (int i = 0; i < data.Length; i += 2)
        {
            ushort word = (ushort)((data[i] << 8) + (i + 1 < data.Length ? data[i + 1] : 0));
            sum += word;
        }

        while (sum >> 16 != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);

        return (ushort)~sum;
    }

    static string ResolveHostname(IPAddress ip)
    {
        try
        {
            IPHostEntry entry = Dns.GetHostEntry(ip);
            return entry.HostName;
        }
        catch
        {
            return null;
        }
    }

    static void Main(string[] args)
    {
        bool resolveNames = false;
        string target = null;
        bool targetIsIp = false;

        foreach (var arg in args)
        {
            if (arg == "-d" || arg == "--resolve")
                resolveNames = true;
            else
                target = arg;
        }

        if (target == null)
        {
            Console.WriteLine("Использование: MyTraceroute [-d] <хост или IP>");
            Console.WriteLine("  -d, --resolve    Разрешать имена узлов (reverse DNS)");
            return;
        }

        // Проверяем, является ли target IP-адресом
        targetIsIp = IPAddress.TryParse(target, out _);

        // Преобразуем имя хоста в IP
        IPAddress destination;
        try
        {
            var addresses = Dns.GetHostAddresses(target);
            destination = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);
            if (destination == null)
            {
                Console.WriteLine("Не удалось разрешить IPv4 адрес для указанного хоста");
                return;
            }

            // Выводим заголовок в зависимости от того, что ввел пользователь
            if (targetIsIp)
            {
                Console.WriteLine($"Трассировка маршрута к {target}");
            }
            else
            {
                Console.WriteLine($"Трассировка маршрута к {target} [{destination}]");
            }
        }
        catch
        {
            Console.WriteLine("Не удалось разрешить имя хоста");
            return;
        }

        Console.WriteLine($"с максимальным числом прыжков 30:\n");

        Socket receiveSocket = null;
        Socket sendSocket = null;

        try
        {
            receiveSocket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
            receiveSocket.Bind(new IPEndPoint(IPAddress.Any, 0));
            receiveSocket.ReceiveTimeout = 3000;

            sendSocket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);

            int maxHops = 30;
            int baseSequence = 1;
            ushort identifier = (ushort)(Process.GetCurrentProcess().Id & 0xFFFF);

            for (int ttl = 1; ttl <= maxHops; ttl++)
            {
                // устанавливаем ttl
                sendSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);

                // запоминаем номер хопа
                string hopNumber = $"{ttl,2}";

                IPAddress currentHop = null;
                bool hopReached = false;
                List<int> responseTimes = new List<int>();

                // Отправляем 3 пакета
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var icmp = new IcmpHeader
                    {
                        Type = 8,
                        Code = 0,
                        Identifier = identifier,
                        SequenceNumber = (ushort)(baseSequence + attempt),
                        Data = new byte[32]
                    };

                    // Заполняем данные
                    for (int i = 0; i < icmp.Data.Length; i++)
                        icmp.Data[i] = (byte)(i + ttl + attempt);

                    byte[] packet = icmp.GetBytes();
                    icmp.Checksum = CalculateChecksum(packet);
                    packet = icmp.GetBytes();

                    var stopwatch = Stopwatch.StartNew(); // Запускаем секундомер для замера RTT

                    try
                    {
                        sendSocket.SendTo(packet, new IPEndPoint(destination, 0));

                        byte[] buffer = new byte[4096];
                        EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

                        if (receiveSocket.ReceiveFrom(buffer, ref remoteEP) > 0)
                        {
                            stopwatch.Stop();

                            int ipHeaderLength = (buffer[0] & 0x0F) * 4;
                            byte icmpType = buffer[ipHeaderLength];

                            IPAddress responder = ((IPEndPoint)remoteEP).Address;

                            if (!hopReached || !responder.Equals(currentHop))
                            {
                                currentHop = responder;
                                hopReached = true;
                            }

                            if (icmpType == 11 || icmpType == 0)
                            {
                                responseTimes.Add((int)stopwatch.ElapsedMilliseconds);

                                // Если достигли цели
                                if (icmpType == 0)
                                {
                                    PrintHop(hopNumber, responseTimes, currentHop, resolveNames, targetIsIp && currentHop.Equals(destination));
                                    Console.WriteLine("\nТрассировка завершена.");
                                    return;
                                }
                            }
                            else
                            {
                                responseTimes.Add(-1);
                            }
                        }
                        else
                        {
                            responseTimes.Add(-1);
                        }
                    }
                    catch
                    {
                        responseTimes.Add(-1);
                    }
                }

                // Выводим результат для текущего хопа
                PrintHop(hopNumber, responseTimes, currentHop, resolveNames, false);

                baseSequence += 3;
            }

            Console.WriteLine("\nПревышен максимальный номер прыжков.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка: {ex.Message}");
            Console.WriteLine("Убедитесь, что программа запущена от имени администратора!");
        }
        finally
        {
            sendSocket?.Close();
            receiveSocket?.Close();
        }
    }

    static void PrintHop(string hopNumber, List<int> times, IPAddress hop, bool resolveNames, bool isDestination)
    {
        // Выводим номер хопа
        Console.Write(hopNumber);

        // Выводим три времени с фиксированной шириной 7 символов каждое
        for (int i = 0; i < 3; i++)
        {
            if (i < times.Count && times[i] >= 0)
            {
                // Для времени: выравниваем по правому краю в пределах 7 символов
                string timeStr = times[i].ToString();
                int paddingLeft = 7 - (timeStr.Length + 3); // +3 для " ms"
                Console.Write(new string(' ', paddingLeft));
                Console.Write($"{timeStr} ms");
            }
            else
            {
                // Для звездочки: ровно 7 символов
                Console.Write("    *  ");
            }
        }

        // Выводим информацию об узле
        if (hop != null)
        {
            string hostname = resolveNames ? ResolveHostname(hop) : null;

            // выводим IP-адрес
            if (!string.IsNullOrEmpty(hostname))
            {
                // Если есть имя - выводим 
                Console.WriteLine($"  {hostname} [{hop}]");
            }
            else
            {
                // Если имени нет - просто IP
                Console.WriteLine($"  {hop}");
            }
        }
        else
        {
            Console.WriteLine("  Превышен интервал ожидания для запроса.");
        }
    }
}