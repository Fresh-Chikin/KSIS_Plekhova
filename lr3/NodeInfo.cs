using System;
using System.Net.Sockets;

namespace P2PChat
{
    public class NodeInfo
    {
        public string Name { get; set; }
        public string Ip { get; set; }
        public int Port { get; set; }
        public DateTime LastSeen { get; set; }
        public TcpClient Client { get; set; }
    }
}