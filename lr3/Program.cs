    using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Linq;
using System.Threading.Tasks;

namespace P2PChat
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Принудительно используем IPv4
            AppContext.SetSwitch("System.Net.DisableIPv6", true);
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            Console.WriteLine("=== P2P Chat System ===");
            Console.WriteLine();

            // Показываем доступные IP адреса
            Console.WriteLine("Available IP addresses on this computer:");
            var hostName = Dns.GetHostName();
            var allAddresses = Dns.GetHostEntry(hostName).AddressList
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .ToList();

            for (int i = 0; i < allAddresses.Count; i++)
            {
                Console.WriteLine($"  {i + 1}. {allAddresses[i]}");
            }
            Console.WriteLine();

            // Запрос IP адреса
            Console.Write("Enter your IP address (127.0.0.1): ");
            string ipInput = Console.ReadLine();
            IPAddress localIp = IPAddress.Parse(ipInput);

            // Запрос TCP порта с проверкой на уникальность
            int tcpPort = await GetUniqueTcpPort(localIp);

            // Запрос UDP порта
            Console.Write("Enter UDP port for broadcast discovery (9999): ");
            int udpPort = int.Parse(Console.ReadLine());

            // Запрос имени пользователя
            Console.Write("Enter your name: ");
            string userName = Console.ReadLine();

            Console.Clear();

            var chatNode = new ChatNode(localIp, tcpPort, udpPort, userName);

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                chatNode.Stop();
            };

            await chatNode.StartAsync();
        }

        static async Task<int> GetUniqueTcpPort(IPAddress localIp)
        {
            while (true)
            {
                Console.Write("Enter your TCP port (unique for this node, 9001): ");
                string input = Console.ReadLine();

                if (!int.TryParse(input, out int port))
                {
                    Console.WriteLine("Invalid port number. Please enter a valid number.");
                    continue;
                }

                if (port < 1024 || port > 65535)
                {
                    Console.WriteLine("Port should be between 1024 and 65535.\n");
                    continue;
                }

                if (IsTcpPortInUse(localIp, port))
                {
                    Console.WriteLine($"ERROR: TCP port {port} is already in use on {localIp}!");
                    Console.WriteLine("Please choose a different port.\n");
                    continue;
                }

                return port;
            }
        }

        static bool IsTcpPortInUse(IPAddress ip, int port)
        {
            try
            {
                var tcpListener = new TcpListener(ip, port);
                tcpListener.Start();
                tcpListener.Stop();
                return false;
            }
            catch (SocketException)
            {
                return true;
            }
            catch
            {
                return true;
            }
        }
    }
}