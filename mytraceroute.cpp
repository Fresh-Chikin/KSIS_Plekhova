
#define _WINSOCK_DEPRECATED_NO_WARNINGS  // Отключаем предупреждения об устаревших функциях
#define _CRT_SECURE_NO_WARNINGS          // Отключаем предупреждения о безопасности
#pragma warning(disable : 28159 28015)   // Отключаем предупреждения о GetTickCount

#include <winsock2.h>     // Основные функции сокетов Windows
#include <ws2tcpip.h>      // Дополнительные функции для работы с IP 
#include <iphlpapi.h>      // Для работы с ICMP-структурами
#include <stdio.h>         // Стандартный ввод/вывод
#include <stdlib.h>        // Стандартные функции
#include <string.h>        // Работа со строками
#include <iostream>        // C++ ввод/вывод
#include <iomanip>         // Для форматирования вывода (setw)
#include <vector>          // Динамические массивы (хотя в итоге не используются)
#include <string>          // C++ строки
#include <clocale>         // Для установки русской локали

#pragma comment(lib, "ws2_32.lib")   // Подключаем библиотеку сокетов
#pragma comment(lib, "iphlpapi.lib") // Подключаем библиотеку IP-помощи

using namespace std;


 //отключает выравнивание, структура точно соответствует пакету
 #pragma pack(1)

 
 //Структура IP-заголовка (20 байт)
  
struct IPHeader {
    UCHAR  iph_verlen;        // Version (4 бита) + Header length (4 бита)
    UCHAR  iph_tos;            // Type of service - тип сервиса (обычно 0)
    USHORT iph_length;         // Общая длина пакета в байтах (заголовок + данные)
    USHORT iph_id;             // Идентификатор пакета (для фрагментации)
    USHORT iph_offset;         // Флаги и смещение фрагментации
    UCHAR  iph_ttl;            // Time to Live 
    UCHAR  iph_protocol;       // Протокол верхнего уровня (1 = ICMP, 6 = TCP, 17 = UDP)
    USHORT iph_xsum;           // Контрольная сумма заголовка 
    ULONG  iph_src;            // IP-адрес отправителя 
    ULONG  iph_dest;           // IP-адрес назначения
};


 //Структура ICMP-заголовка (8 байт для Echo запроса)
struct ICMPHeader {
    UCHAR  icmp_type;          // Тип ICMP-сообщения
    UCHAR  icmp_code;          // Код (для Echo всегда 0, для Time Exceeded тоже обычно 0)
    USHORT icmp_cksum;         // Контрольная сумма всего ICMP-пакета 
    USHORT icmp_id;            // Идентификатор (обычно PID процесса, чтобы отличать свои пакеты)
    USHORT icmp_seq;           // Номер последовательности (увеличивается с каждым пакетом)
};


 // Полезная нагрузка ICMP-пакета (наши данные)
struct ICMPPayload {
    LONGLONG timestamp;        // Время отправки 
    char  padding[24];         // Дополнительные байты для увеличения размера пакета
    
};


 // Полный ICMP Echo пакет = заголовок + данные

struct ICMPEchoPacket {
    ICMPHeader header;
    ICMPPayload payload;
};

#pragma pack()  // Возвращаем стандартное выравнивание


LARGE_INTEGER perfFreq;  // Частота высокоточного таймера (тиков в секунду)
// Нужна для перевода тиков в миллисекунды

/**
 * Подсчет контрольной суммы (RFC 1071)
 *
 * Принцип работы:
 * 1. Разбиваем данные на 16-битные слова
 * 2. Складываем все слова как 16-битные числа с циклическим переносом
 * 3. Инвертируем полученный результат
 *
 * Если контрольная сумма не совпадает,
 * получатель отбрасывает пакет как поврежденный
 *
 * @param buffer указатель на данные
 * @param size размер данных в байтах
 * @return 16-битная контрольная сумма
 */
USHORT calculateChecksum(USHORT* buffer, int size) {
    ULONG cksum = 0;

    // Суммируем все 16-битные слова
    while (size > 1) {
        cksum += *buffer++;
        size -= sizeof(USHORT);
    }

    // Если остался нечетный байт, добавляем его
    if (size) {
        cksum += *(UCHAR*)buffer;
    }

    // Добавляем переносы из старших 16 бит в младшие
    cksum = (cksum >> 16) + (cksum & 0xffff);
    cksum += (cksum >> 16);  // Может возникнуть еще один перенос

    return (USHORT)(~cksum);  // Инвертируем все биты
}

/**
 * Разрешение доменного имени в IP-адрес
 *
 * @param host строка с именем (например "google.com" или "8.8.8.8")
 * @param ipAddr указатель для сохранения результата
 * @return true если успешно, false если ошибка
 */
bool resolveHostname(const char* host, in_addr* ipAddr) {
    // Сначала пробуем интерпретировать как IP-адрес 
    if (inet_pton(AF_INET, host, ipAddr) == 1) {
        return true;  // Это был IP-адрес
    }

    // Если не IP, значит это доменное имя - запрашиваем DNS
    struct hostent* remoteHost = gethostbyname(host);
    if (remoteHost == NULL) {
        cerr << "[-] Не удалось разрешить имя хоста: " << host << endl;
        return false;
    }

    // Берем первый IP-адрес из списка 
    if (remoteHost->h_addr_list[0] != NULL) {
        memcpy(ipAddr, remoteHost->h_addr_list[0], remoteHost->h_length);
        return true;
    }

    return false;
}

/**
 * Обратное DNS-разрешение (по IP узнаем имя хоста)
 *
 * @param ip строка с IP-адресом
 * @return имя хоста или сам IP, если имя не найдено
 */
string reverseDNS(const char* ip) {
    char hostname[NI_MAXHOST] = "";
    struct sockaddr_in sa;
    sa.sin_family = AF_INET;
    inet_pton(AF_INET, ip, &sa.sin_addr);

    // getnameinfo выполняет обратный DNS-запрос
    int result = getnameinfo((struct sockaddr*)&sa, sizeof(sa),
        hostname, NI_MAXHOST, NULL, 0, 0);

    if (result == 0) {
        return string(hostname);  // Нашли имя
    }
    return string(ip);  // Имя не найдено, возвращаем IP
}

int main(int argc, char* argv[]) {
    
    setlocale(LC_ALL, "Russian");

    // Инициализация высокоточного таймера
    QueryPerformanceFrequency(&perfFreq);

    
    // Инициализация Winsock
   
    WSADATA wsaData;
    if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0) {
        cerr << "[-] Ошибка инициализации Winsock" << endl;
        return 1;
    }

    // парсинг аргументов командной строки
    if (argc < 2) {
        cout << "Использование: " << argv[0] << " <цель> [-d]" << endl;
        cout << "  <цель>     - IP-адрес или доменное имя" << endl;
        cout << "  -d         - Включить обратное DNS-разрешение" << endl;
        WSACleanup();
        return 1;
    }

    string target = argv[1];                 // Целевой узел
    bool reverseLookup = (argc >= 3 && strcmp(argv[2], "-d") == 0);  // Флаг DNS

    // разрешение целевого узла
    in_addr destIP;
    if (!resolveHostname(target.c_str(), &destIP)) {
        WSACleanup();
        return 1;
    }

    // Преобразуем IP в строку для вывода
    char destIPStr[INET_ADDRSTRLEN];
    inet_ntop(AF_INET, &destIP, destIPStr, sizeof(destIPStr));

    //Создание сокета
    
    SOCKET icmpSocket = socket(AF_INET, SOCK_RAW, IPPROTO_ICMP);
    if (icmpSocket == INVALID_SOCKET) {
        cerr << "[-] Не удалось создать RAW-сокет. Запустите от имени Администратора!" << endl;
        WSACleanup();
        return 1;
    }

    // Установка таймаута
    // SO_RCVTIMEO - опция таймаута на прием
    // Если ответ не придет за 3 секунды, recvfrom вернет ошибку WSAETIMEDOUT
    int timeout = 3000;  // 3000 миллисекунд = 3 секунды
    setsockopt(icmpSocket, SOL_SOCKET, SO_RCVTIMEO, (const char*)&timeout, sizeof(timeout));

    // подготовка адреса назначения
    sockaddr_in destAddr;
    destAddr.sin_family = AF_INET;    // IPv4
    destAddr.sin_port = 0;             // Для ICMP порт не используется
    destAddr.sin_addr = destIP;        // IP целевого узла

    // Параметры трассировки
    const int MAX_HOPS = 30;        // Максимальное число прыжков 
    const int TRIES_PER_HOP = 3;    // Три попытки на каждом хопе
    USHORT processID = (USHORT)GetCurrentProcessId();  // PID для идентификации наших пакетов

    
    cout << "\nТрассировка маршрута к " << target << " [" << destIPStr << "]" << endl;
    cout << "с максимальным числом прыжков " << MAX_HOPS << ":\n" << endl;

    bool reachedDestination = false;  // Флаг достижения цели

   
    for (int ttl = 1; ttl <= MAX_HOPS && !reachedDestination; ttl++) {
        setsockopt(icmpSocket, IPPROTO_IP, IP_TTL, (const char*)&ttl, sizeof(ttl));

        // Массивы для хранения результатов трех попыток
        ULONG rttTimes[3] = { 0, 0, 0 };        // Времена ответа
        string responderIPs[3] = { "", "", "" }; // IP-адреса ответивших
        bool hasResponse[3] = { false, false, false }; // Флаги наличия ответа

        // 3 попытки для текущего TTL
        for (int attempt = 0; attempt < TRIES_PER_HOP; attempt++) {
            // Формирование  ICMP - пакета
            ICMPEchoPacket packet;
            memset(&packet, 0, sizeof(packet));  // Обнуляем память

            packet.header.icmp_type = 8;           // Тип 8 = Echo Request
            packet.header.icmp_code = 0;            // Код 0
            packet.header.icmp_id = processID;      // Наш PID
            // Уникальный sequence для каждой попытки
            packet.header.icmp_seq = (ttl - 1) * TRIES_PER_HOP + attempt + 1;

            // Запоминаем время отправки
            LARGE_INTEGER sendTime;
            QueryPerformanceCounter(&sendTime);
            memcpy(&packet.payload.timestamp, &sendTime, sizeof(sendTime));

            // подсчет контрольной суммы
            packet.header.icmp_cksum = calculateChecksum((USHORT*)&packet, sizeof(packet));

            // отправляем пакет
            sendto(icmpSocket, (const char*)&packet, sizeof(packet), 0,
                (sockaddr*)&destAddr, sizeof(destAddr));

            // ожидание ответа
            char recvBuffer[4096];                  // Буфер для приема
            sockaddr_in senderAddr;                  // Адрес отправителя ответа
            int senderAddrSize = sizeof(senderAddr);

            int bytesReceived = recvfrom(icmpSocket, recvBuffer, sizeof(recvBuffer), 0,
                (sockaddr*)&senderAddr, &senderAddrSize);

            if (bytesReceived == SOCKET_ERROR) {
                // Таймаут или ошибка - пропускаем попытку
                Sleep(200);
                continue;
            }

            if (bytesReceived < 20) continue;  // Слишком маленький пакет

            // анализ IP-заголовка пакета
            IPHeader* ipHeader = (IPHeader*)recvBuffer;
            int ipHeaderLen = (ipHeader->iph_verlen & 0x0F) * 4;  // Длина IP-заголовка

            if (ipHeader->iph_protocol != 1) continue;  // Не ICMP - пропускаем

            ICMPHeader* icmpHeader = (ICMPHeader*)(recvBuffer + ipHeaderLen);

            
             // обработка TIME EXCEEDED (тип 11)
             
             
            if (icmpHeader->icmp_type == 11) {
                // Смещение до внутреннего IP-заголовка (отправленный пакет)
                int innerIPOffset = ipHeaderLen + 8;
                if (bytesReceived < innerIPOffset + 20) continue;

                IPHeader* innerIP = (IPHeader*)(recvBuffer + innerIPOffset);
                int innerIPHeaderLen = (innerIP->iph_verlen & 0x0F) * 4;

                // Смещение до внутреннего ICMP-заголовка
                int innerICMPOffset = innerIPOffset + innerIPHeaderLen;
                if (bytesReceived < innerICMPOffset + 8) continue;

                ICMPHeader* innerICMP = (ICMPHeader*)(recvBuffer + innerICMPOffset);

                // Проверяем, что это наш пакет (по ID и sequence)
                if (innerICMP->icmp_id != processID) continue;
                if (innerICMP->icmp_seq != packet.header.icmp_seq) continue;

                // Сохраняем IP ответившего маршрутизатора
                char ipStr[INET_ADDRSTRLEN];
                inet_ntop(AF_INET, &senderAddr.sin_addr, ipStr, sizeof(ipStr));
                responderIPs[attempt] = string(ipStr);
                hasResponse[attempt] = true;

                // Извлекаем время отправки и вычисляем RTT
                int innerPayloadOffset = innerICMPOffset + 8;
                if (bytesReceived >= innerPayloadOffset + sizeof(LONGLONG)) {
                    LARGE_INTEGER sentTime;
                    memcpy(&sentTime, recvBuffer + innerPayloadOffset, sizeof(sentTime));

                    LARGE_INTEGER currentTime;
                    QueryPerformanceCounter(&currentTime);

                    // Разница в тиках
                    LONGLONG diff = currentTime.QuadPart - sentTime.QuadPart;
                    // Переводим в миллисекунды: (тики * 1000) / частота_таймера
                    double ms = (double)diff * 1000.0 / perfFreq.QuadPart;
                    rttTimes[attempt] = (ULONG)(ms + 0.5);  // Округляем
                }
            }

            
            // обработка ECHO REPLY (тип 0)
            if (icmpHeader->icmp_type == 0) {
                if (icmpHeader->icmp_id != processID) continue;
                if (icmpHeader->icmp_seq != packet.header.icmp_seq) continue;

                reachedDestination = true;  // Сигнал к завершению

                // Сохраняем IP целевого узла
                char ipStr[INET_ADDRSTRLEN];
                inet_ntop(AF_INET, &senderAddr.sin_addr, ipStr, sizeof(ipStr));
                responderIPs[attempt] = string(ipStr);
                hasResponse[attempt] = true;

                // Извлекаем время и вычисляем RTT
                int payloadOffset = ipHeaderLen + 8;
                if (bytesReceived >= payloadOffset + sizeof(LONGLONG)) {
                    LARGE_INTEGER sentTime;
                    memcpy(&sentTime, recvBuffer + payloadOffset, sizeof(sentTime));

                    LARGE_INTEGER currentTime;
                    QueryPerformanceCounter(&currentTime);

                    LONGLONG diff = currentTime.QuadPart - sentTime.QuadPart;
                    double ms = (double)diff * 1000.0 / perfFreq.QuadPart;
                    rttTimes[attempt] = (ULONG)(ms + 0.5);
                }
            }

            Sleep(200);  // Небольшая задержка между попытками
        }

        // вывод результата для текущего хопа

        // Выводим номер хопа 
        cout << "  " << setw(2) << ttl << "  ";

        // Выводим времена ответа для трех попыток
        for (int i = 0; i < TRIES_PER_HOP; i++) {
            if (rttTimes[i] == 0) {
                cout << "   *   ";  // Звездочка - нет ответа
            }
            else {
                cout << " " << setw(2) << rttTimes[i] << "ms ";
            }
        }

        // Определяем IP для вывода (берем первый успешный ответ)
        string displayIP = "";
        for (int i = 0; i < TRIES_PER_HOP; i++) {
            if (hasResponse[i]) {
                displayIP = responderIPs[i];
                break;
            }
        }

        // Выводим IP (с именем, если запрошено)
        if (!displayIP.empty()) {
            if (reverseLookup) {
                string hostname = reverseDNS(displayIP.c_str());
                if (hostname != displayIP) {
                    cout << "  " << hostname << " [" << displayIP << "]";
                }
                else {
                    cout << "  " << displayIP;
                }
            }
            else {
                cout << "  " << displayIP;
            }
        }

        cout << endl;  

        // Задержка между хопами 
        Sleep(1000);
    }

    // завершение работы
    closesocket(icmpSocket);  // Закрываем сокет
    WSACleanup();             // Освобождаем ресурсы Winsock

    cout << "\nТрассировка завершена." << endl;
    return 0;
}