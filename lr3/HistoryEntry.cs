using System;

namespace P2PChat
{
    public class HistoryEntry
    {
        public DateTime Timestamp { get; set; }
        public string Type { get; set; }
        public string SenderName { get; set; }
        public string SenderIp { get; set; }
        public string Content { get; set; }
        public string TargetName { get; set; }
        public string TargetIp { get; set; }

        // Для отображения в чате 
        public override string ToString()
        {
            string prefix = Type switch
            {
                "message" => $"[{SenderName}]: {Content}",
                "self" => $"[You]: {Content}",
                "session_start" => Content,
                "session_end" => Content,
                _ => Content
            };
            return $"[{Timestamp:HH:mm:ss}] {prefix}";
        }

        // Для отображения в истории 
        public string ToHistoryString()
        {
            string prefix = Type switch
            {
                "message" => $"[{SenderName} ({SenderIp})]: {Content}",
                "self" => $"[{SenderName} ({SenderIp})]: {Content}",
                "session_start" => Content,
                "session_end" => Content,
                _ => Content
            };
            return $"[{Timestamp:HH:mm:ss}] {prefix}";
        }

        // Для записи в файл
        public string ToFileString()
        {
            string prefix = Type switch
            {
                "message" => $"[{SenderName} ({SenderIp})]: {Content}",
                "self" => $"[{SenderName} ({SenderIp})]: {Content}",
                "session_start" => Content,
                "session_end" => Content,
                _ => Content
            };
            return $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] {prefix}";
        }
    }
}