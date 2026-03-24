using System;
using System.Text;
using System.Text.Json;

namespace P2PChat
{
    public enum MessageType : byte
    {
        TextMessage = 1,
        NameExchange = 2,
        UserConnected = 3,
        UserDisconnected = 4,
        HistoryRequest = 5,
        HistoryResponse = 6,
        Ping = 7,
        Pong = 8
    }

    public class ChatMessage
    {
        public MessageType Type { get; set; }
        public string Content { get; set; }
        public string SenderName { get; set; }
        public string SenderIp { get; set; }
        public int SenderTcpPort { get; set; }
        public DateTime Timestamp { get; set; }

        public ChatMessage()
        {
            Timestamp = DateTime.Now;
        }

        public byte[] Serialize()
        {
            var json = JsonSerializer.Serialize(this);
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            int length = jsonBytes.Length;

            // Заголовок: 1 байт тип + 2 байта длина = 3 байта
            var result = new byte[3 + length];

            result[0] = (byte)Type;
            result[1] = (byte)(length >> 8);   // старший байт длины
            result[2] = (byte)(length & 0xFF); // младший байт длины

            Array.Copy(jsonBytes, 0, result, 3, length);
            return result;
        }

        public static ChatMessage Deserialize(byte[] data)
        {
            if (data.Length < 3) return null;

            var type = (MessageType)data[0];
            int length = (data[1] << 8) | data[2];

            // если в буфере не хватает данных 
            if (data.Length < 3 + length) return null;

            try
            {
                var json = Encoding.UTF8.GetString(data, 3, length);
                var message = JsonSerializer.Deserialize<ChatMessage>(json);
                if (message != null)
                    message.Type = type;
                return message;
            }
            catch
            {
                return null;
            }
        }
    }
}