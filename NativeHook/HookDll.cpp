// HookDll.cpp - Native Hook DLL for intercepting network APIs
// Using MinHook to intercept WinSock, WinHTTP and SSL/TLS APIs

#define WIN32_LEAN_AND_MEAN
#define SECURITY_WIN32
#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <winhttp.h>
#include <wininet.h>
#include <security.h>
#include <sspi.h>
#include <string>
#include <sstream>
#include <mutex>
#include <algorithm>
#include <atomic>
#include "MinHook.h"

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "winhttp.lib")
#pragma comment(lib, "wininet.lib")
#pragma comment(lib, "secur32.lib")

// 全局变量
static HANDLE g_hPipe = INVALID_HANDLE_VALUE;
static std::mutex g_pipeMutex;
static bool g_initialized = false;

// 函数前向声明
void SendLog(const char* message);
void SendCapturedData(const char* data, int length, bool isSend);
bool CheckForStreamInfo(const char* data, int length);

// 函数指针类型定义
typedef int (WINAPI* SEND_FUNC)(SOCKET s, const char* buf, int len, int flags);
typedef int (WINAPI* RECV_FUNC)(SOCKET s, char* buf, int len, int flags);
typedef int (WINAPI* WSASEND_FUNC)(SOCKET s, LPWSABUF lpBuffers, DWORD dwBufferCount,
                                    LPDWORD lpNumberOfBytesSent, DWORD dwFlags,
                                    LPWSAOVERLAPPED lpOverlapped,
                                    LPWSAOVERLAPPED_COMPLETION_ROUTINE lpCompletionRoutine);
typedef int (WINAPI* WSARECV_FUNC)(SOCKET s, LPWSABUF lpBuffers, DWORD dwBufferCount,
                                    LPDWORD lpNumberOfBytesRecvd, LPDWORD lpFlags,
                                    LPWSAOVERLAPPED lpOverlapped,
                                    LPWSAOVERLAPPED_COMPLETION_ROUTINE lpCompletionRoutine);

typedef HINTERNET (WINAPI* WINHTTPOPEN_FUNC)(LPCWSTR, DWORD, LPCWSTR, LPCWSTR, DWORD);
typedef BOOL (WINAPI* WINHTTPSENDRREQUEST_FUNC)(HINTERNET, LPCWSTR, DWORD, LPVOID, DWORD, DWORD, DWORD_PTR);
typedef BOOL (WINAPI* WINHTTPRECEIVERESPONSE_FUNC)(HINTERNET, LPVOID);
typedef BOOL (WINAPI* WINHTTPREADDATA_FUNC)(HINTERNET, LPVOID, DWORD, LPDWORD);
typedef BOOL (WINAPI* WINHTTPWRITEDATA_FUNC)(HINTERNET, LPCVOID, DWORD, LPDWORD);

// WinINet function pointer types
typedef BOOL (WINAPI* INTERNETREADFILE_FUNC)(HINTERNET, LPVOID, DWORD, LPDWORD);
typedef BOOL (WINAPI* INTERNETWRITEFILE_FUNC)(HINTERNET, LPCVOID, DWORD, LPDWORD);
typedef BOOL (WINAPI* HTTPSENDREQUEST_FUNC)(HINTERNET, LPCSTR, DWORD, LPVOID, DWORD);

// OpenSSL 函数指针类型 (动态加载)
typedef int (*SSL_READ_FUNC)(void* ssl, void* buf, int num);
typedef int (*SSL_WRITE_FUNC)(void* ssl, const void* buf, int num);

// Schannel 函数指针类型
typedef SECURITY_STATUS (WINAPI* DECRYPT_MESSAGE_FUNC)(PCtxtHandle, PSecBufferDesc, ULONG, PULONG);
typedef SECURITY_STATUS (WINAPI* ENCRYPT_MESSAGE_FUNC)(PCtxtHandle, ULONG, PSecBufferDesc, ULONG);

// 原始函数指针
static SEND_FUNC g_originalSend = nullptr;
static RECV_FUNC g_originalRecv = nullptr;
static WSASEND_FUNC g_originalWSASend = nullptr;
static WSARECV_FUNC g_originalWSARecv = nullptr;
static WINHTTPOPEN_FUNC g_originalWinHttpOpen = nullptr;
static WINHTTPSENDRREQUEST_FUNC g_originalWinHttpSendRequest = nullptr;
static WINHTTPRECEIVERESPONSE_FUNC g_originalWinHttpReceiveResponse = nullptr;
static WINHTTPREADDATA_FUNC g_originalWinHttpReadData = nullptr;
static WINHTTPWRITEDATA_FUNC g_originalWinHttpWriteData = nullptr;

// WinINet original function pointers
static INTERNETREADFILE_FUNC g_originalInternetReadFile = nullptr;
static INTERNETWRITEFILE_FUNC g_originalInternetWriteFile = nullptr;
static HTTPSENDREQUEST_FUNC g_originalHttpSendRequest = nullptr;

// OpenSSL 原始函数指针
static SSL_READ_FUNC g_originalSSL_read = nullptr;
static SSL_WRITE_FUNC g_originalSSL_write = nullptr;

// Schannel 原始函数指针
static DECRYPT_MESSAGE_FUNC g_originalDecryptMessage = nullptr;

// 辅助函数：连接到命名管道
bool ConnectToPipe(const char* pipeName)
{
    {
        std::lock_guard<std::mutex> lock(g_pipeMutex);

        if (g_hPipe != INVALID_HANDLE_VALUE)
            return true;
    }

    char fullPipeName[256];
    sprintf_s(fullPipeName, "\\\\.\\pipe\\%s", pipeName);

    for (int retry = 0; retry < 5; retry++)
    {
        HANDLE hPipe = CreateFileA(
            fullPipeName,
            GENERIC_READ | GENERIC_WRITE,
            0,
            NULL,
            OPEN_EXISTING,
            0,
            NULL);

        if (hPipe != INVALID_HANDLE_VALUE)
        {
            // 先读取握手消息 (C# BinaryWriter.Write(string) 格式：长度前缀 + 字符串)
            char hello[16] = {0};
            DWORD bytesRead;

            // 读取长度前缀（7位编码）
            BYTE lengthByte;
            if (ReadFile(hPipe, &lengthByte, 1, &bytesRead, NULL) && bytesRead > 0)
            {
                int length = lengthByte & 0x7F;  // 对于短字符串，长度< 128，只需要1字节

                // 读取字符串内容
                if (length > 0 && length < sizeof(hello))
                {
                    ReadFile(hPipe, hello, length, &bytesRead, NULL);
                }
            }

            // 握手成功，设置全局pipe handle
            {
                std::lock_guard<std::mutex> lock(g_pipeMutex);
                g_hPipe = hPipe;
            }

            // 现在可以安全调用SendLog（锁已释放）
            SendLog("[Native Hook] IPC connected successfully!");

            return true;
        }

        Sleep(1000);
    }

    return false;
}

// 辅助函数：发送日志
void SendLog(const char* message)
{
    if (g_hPipe == INVALID_HANDLE_VALUE)
        return;

    std::lock_guard<std::mutex> lock(g_pipeMutex);

    BYTE msgType = 1; // 日志消息
    DWORD bytesWritten;

    // 写入消息类型
    if (!WriteFile(g_hPipe, &msgType, 1, &bytesWritten, NULL))
    {
        // Pipe断开
        CloseHandle(g_hPipe);
        g_hPipe = INVALID_HANDLE_VALUE;
        return;
    }

    // 写入字符串长度（7位编码，兼容 .NET BinaryReader）
    int len = (int)strlen(message);
    unsigned int v = (unsigned int)len;
    while (v >= 0x80)
    {
        BYTE b = (BYTE)((v & 0x7F) | 0x80);
        if (!WriteFile(g_hPipe, &b, 1, &bytesWritten, NULL))
        {
            CloseHandle(g_hPipe);
            g_hPipe = INVALID_HANDLE_VALUE;
            return;
        }
        v >>= 7;
    }
    BYTE lastByte = (BYTE)v;
    if (!WriteFile(g_hPipe, &lastByte, 1, &bytesWritten, NULL))
    {
        CloseHandle(g_hPipe);
        g_hPipe = INVALID_HANDLE_VALUE;
        return;
    }

    // 写入字符串内容
    if (!WriteFile(g_hPipe, message, len, &bytesWritten, NULL))
    {
        CloseHandle(g_hPipe);
        g_hPipe = INVALID_HANDLE_VALUE;
        return;
    }

    FlushFileBuffers(g_hPipe);
}

// 辅助函数：检查数据中是否包含RTMP URL或快手推流相关信息
bool CheckForStreamInfo(const char* data, int length)
{
    if (!data || length < 10)
        return false;

    int checkLen = (length < 8192) ? length : 8192; // 只检查前8KB
    std::string text(data, checkLen);
    std::transform(text.begin(), text.end(), text.begin(), ::tolower);

    // 检查是否包含推流相关关键字
    static const char* keywords[] = {
        "rtmp://", "rtmps://", "rtmpUrl", "pushUrl", "push_url",
        "streamUrl", "stream_url", "live-push", "open-push",
        ".kuaishou.com", ".ksyun.com", ".yximgs.com",
        "\"url\":", "\"rtmp", "\"push"
    };

    for (const char* keyword : keywords)
    {
        if (text.find(keyword) != std::string::npos)
        {
            return true;
        }
    }

    return false;
}

// 辅助函数：发送捕获的数据
void SendCapturedData(const char* data, int length, bool isSend)
{
    if (g_hPipe == INVALID_HANDLE_VALUE || length <= 0 || length > 65536)
        return;

    std::lock_guard<std::mutex> lock(g_pipeMutex);

    BYTE msgType = isSend ? 2 : 3; // 2=发送数据, 3=接收数据
    DWORD bytesWritten;

    // 写入消息类型
    if (!WriteFile(g_hPipe, &msgType, 1, &bytesWritten, NULL))
    {
        CloseHandle(g_hPipe);
        g_hPipe = INVALID_HANDLE_VALUE;
        return;
    }

    // 写入数据长度（4字节int32，兼容C# BinaryReader.ReadInt32）
    if (!WriteFile(g_hPipe, &length, 4, &bytesWritten, NULL))
    {
        CloseHandle(g_hPipe);
        g_hPipe = INVALID_HANDLE_VALUE;
        return;
    }

    // 写入数据内容
    if (!WriteFile(g_hPipe, data, length, &bytesWritten, NULL))
    {
        CloseHandle(g_hPipe);
        g_hPipe = INVALID_HANDLE_VALUE;
        return;
    }

    FlushFileBuffers(g_hPipe);

    // 如果包含推流信息，记录日志（递归调用SendLog，但已释放锁）
    // 注意：这里不能调用SendLog，因为还持有锁！应该在锁外调用
}

// Hook: send
int WINAPI HookedSend(SOCKET s, const char* buf, int len, int flags)
{
    // 捕获发送的数据
    if (buf && len > 0 && len <= 65536)
    {
        SendCapturedData(buf, len, true);
    }

    // 调用原始函数
    return g_originalSend(s, buf, len, flags);
}

// Hook: recv
int WINAPI HookedRecv(SOCKET s, char* buf, int len, int flags)
{
    // 调用原始函数
    int result = g_originalRecv(s, buf, len, flags);

    // 捕获接收的数据
    if (result > 0 && buf)
    {
        SendCapturedData(buf, result, false);
    }

    return result;
}

// Hook: WSASend
int WINAPI HookedWSASend(SOCKET s, LPWSABUF lpBuffers, DWORD dwBufferCount,
                          LPDWORD lpNumberOfBytesSent, DWORD dwFlags,
                          LPWSAOVERLAPPED lpOverlapped,
                          LPWSAOVERLAPPED_COMPLETION_ROUTINE lpCompletionRoutine)
{
    // 捕获发送的数据
    if (lpBuffers && dwBufferCount > 0)
    {
        for (DWORD i = 0; i < dwBufferCount && i < 10; i++)
        {
            if (lpBuffers[i].buf && lpBuffers[i].len > 0 && lpBuffers[i].len <= 65536)
            {
                SendCapturedData(lpBuffers[i].buf, lpBuffers[i].len, true);
            }
        }
    }

    // 调用原始函数
    return g_originalWSASend(s, lpBuffers, dwBufferCount, lpNumberOfBytesSent,
                              dwFlags, lpOverlapped, lpCompletionRoutine);
}

// Hook: WSARecv
int WINAPI HookedWSARecv(SOCKET s, LPWSABUF lpBuffers, DWORD dwBufferCount,
                          LPDWORD lpNumberOfBytesRecvd, LPDWORD lpFlags,
                          LPWSAOVERLAPPED lpOverlapped,
                          LPWSAOVERLAPPED_COMPLETION_ROUTINE lpCompletionRoutine)
{
    // 调用原始函数
    int result = g_originalWSARecv(s, lpBuffers, dwBufferCount, lpNumberOfBytesRecvd,
                                     lpFlags, lpOverlapped, lpCompletionRoutine);

    // 捕获接收的数据（同步模式）
    if (result == 0 && !lpOverlapped && lpBuffers && lpNumberOfBytesRecvd)
    {
        DWORD totalBytes = *lpNumberOfBytesRecvd;
        for (DWORD i = 0; i < dwBufferCount && i < 10 && totalBytes > 0; i++)
        {
            DWORD bytes = min(totalBytes, lpBuffers[i].len);
            if (lpBuffers[i].buf && bytes > 0 && bytes <= 65536)
            {
                SendCapturedData(lpBuffers[i].buf, bytes, false);
            }
            totalBytes -= bytes;
        }
    }

    return result;
}

// Hook: OpenSSL SSL_read (解密接收的HTTPS数据)
int HookedSSL_read(void* ssl, void* buf, int num)
{
    // 调用原始函数
    int result = g_originalSSL_read(ssl, buf, num);

    // 捕获解密后的数据
    if (result > 0 && buf)
    {
        SendCapturedData((const char*)buf, result, false);
    }

    return result;
}

// Hook: OpenSSL SSL_write (加密发送的HTTPS数据)
int HookedSSL_write(void* ssl, const void* buf, int num)
{
    // 捕获未加密的数据
    if (buf && num > 0 && num <= 65536)
    {
        SendCapturedData((const char*)buf, num, true);
    }

    // 调用原始函数
    return g_originalSSL_write(ssl, buf, num);
}

// Hook: Schannel DecryptMessage
SECURITY_STATUS WINAPI HookedDecryptMessage(PCtxtHandle phContext, PSecBufferDesc pMessage, ULONG MessageSeqNo, PULONG pfQOP)
{
    // 调用原始函数进行解密
    SECURITY_STATUS result = g_originalDecryptMessage(phContext, pMessage, MessageSeqNo, pfQOP);

    // 如果解密成功，提取明文数据
    if (result == SEC_E_OK && pMessage != nullptr)
    {
        // 遍历所有缓冲区，查找包含解密数据的SECBUFFER_DATA
        for (ULONG i = 0; i < pMessage->cBuffers; i++)
        {
            SecBuffer* pBuffer = &pMessage->pBuffers[i];

            // SECBUFFER_DATA (1) 包含解密后的应用数据
            if (pBuffer->BufferType == SECBUFFER_DATA &&
                pBuffer->pvBuffer != nullptr &&
                pBuffer->cbBuffer > 0)
            {
                // 提取解密后的明文数据
                // 无条件发送所有解密数据（调试）
                SendCapturedData(
                    static_cast<const char*>(pBuffer->pvBuffer),
                    pBuffer->cbBuffer,
                    false  // 接收的数据
                );

                // 调试日志：记录解密数据量（线程安全）
                static std::atomic<int> decryptCount(0);
                int currentCount = ++decryptCount;
                if (currentCount % 50 == 1) // 每50次记录一次，避免日志爆炸
                {
                    char logMsg[256];
                    sprintf_s(logMsg, "[Schannel] DecryptMessage called %d times, latest: %d bytes",
                             currentCount, pBuffer->cbBuffer);
                    SendLog(logMsg);
                }
            }
        }
    }

    return result;
}

// Hook: WinHttpReadData (读取HTTP响应数据)
BOOL WINAPI HookedWinHttpReadData(HINTERNET hRequest, LPVOID lpBuffer, DWORD dwNumberOfBytesToRead, LPDWORD lpdwNumberOfBytesRead)
{
    // 调用原始函数
    BOOL result = g_originalWinHttpReadData(hRequest, lpBuffer, dwNumberOfBytesToRead, lpdwNumberOfBytesRead);

    // 如果读取成功，捕获数据
    if (result && lpdwNumberOfBytesRead && *lpdwNumberOfBytesRead > 0 && lpBuffer)
    {
        SendCapturedData(static_cast<const char*>(lpBuffer), *lpdwNumberOfBytesRead, false);
    }

    return result;
}

// Hook: WinHttpWriteData (发送HTTP请求数据)
BOOL WINAPI HookedWinHttpWriteData(HINTERNET hRequest, LPCVOID lpBuffer, DWORD dwNumberOfBytesToWrite, LPDWORD lpdwNumberOfBytesWritten)
{
    // 捕获发送的数据
    if (lpBuffer && dwNumberOfBytesToWrite > 0 && dwNumberOfBytesToWrite <= 65536)
    {
        SendCapturedData(static_cast<const char*>(lpBuffer), dwNumberOfBytesToWrite, true);
    }

    // 调用原始函数
    return g_originalWinHttpWriteData(hRequest, lpBuffer, dwNumberOfBytesToWrite, lpdwNumberOfBytesWritten);
}

// Hook: InternetReadFile (WinINet - 读取HTTP响应数据)
BOOL WINAPI HookedInternetReadFile(HINTERNET hFile, LPVOID lpBuffer, DWORD dwNumberOfBytesToRead, LPDWORD lpdwNumberOfBytesRead)
{
    // 调用原始函数
    BOOL result = g_originalInternetReadFile(hFile, lpBuffer, dwNumberOfBytesToRead, lpdwNumberOfBytesRead);

    // 如果读取成功，捕获数据
    if (result && lpdwNumberOfBytesRead && *lpdwNumberOfBytesRead > 0 && lpBuffer)
    {
        SendCapturedData(static_cast<const char*>(lpBuffer), *lpdwNumberOfBytesRead, false);
    }

    return result;
}

// Hook: InternetWriteFile (WinINet - 发送HTTP请求数据)
BOOL WINAPI HookedInternetWriteFile(HINTERNET hFile, LPCVOID lpBuffer, DWORD dwNumberOfBytesToWrite, LPDWORD lpdwNumberOfBytesWritten)
{
    // 捕获发送的数据
    if (lpBuffer && dwNumberOfBytesToWrite > 0 && dwNumberOfBytesToWrite <= 65536)
    {
        SendCapturedData(static_cast<const char*>(lpBuffer), dwNumberOfBytesToWrite, true);
    }

    // 调用原始函数
    return g_originalInternetWriteFile(hFile, lpBuffer, dwNumberOfBytesToWrite, lpdwNumberOfBytesWritten);
}

// Hook: HttpSendRequest (WinINet - 发送HTTP请求)
BOOL WINAPI HookedHttpSendRequest(HINTERNET hRequest, LPCSTR lpszHeaders, DWORD dwHeadersLength, LPVOID lpOptional, DWORD dwOptionalLength)
{
    // 捕获可选的请求体数据
    if (lpOptional && dwOptionalLength > 0 && dwOptionalLength <= 65536)
    {
        SendCapturedData(static_cast<const char*>(lpOptional), dwOptionalLength, true);
    }

    // 调用原始函数
    return g_originalHttpSendRequest(hRequest, lpszHeaders, dwHeadersLength, lpOptional, dwOptionalLength);
}

// 辅助函数：尝试Hook OpenSSL函数
void TryHookOpenSSL()
{
    // 尝试多个可能的OpenSSL DLL名称
    const char* sslDlls[] = {
        "libssl-3-x64.dll", "libssl-1_1-x64.dll", "libssl.dll",
        "ssleay32.dll", "libssl-3.dll", "libssl-1_1.dll"
    };

    for (const char* dllName : sslDlls)
    {
        HMODULE hSSL = GetModuleHandleA(dllName);
        if (!hSSL)
            continue;

        // 尝试获取SSL_read和SSL_write函数地址
        void* ssl_read_addr = GetProcAddress(hSSL, "SSL_read");
        void* ssl_write_addr = GetProcAddress(hSSL, "SSL_write");

        if (ssl_read_addr)
        {
            if (MH_CreateHook(ssl_read_addr, &HookedSSL_read,
                             reinterpret_cast<LPVOID*>(&g_originalSSL_read)) == MH_OK)
            {
                if (MH_EnableHook(ssl_read_addr) == MH_OK)
                {
                    char msg[256];
                    sprintf_s(msg, "[Native Hook] Successfully hooked SSL_read in %s", dllName);
                    SendLog(msg);
                }
            }
        }

        if (ssl_write_addr)
        {
            if (MH_CreateHook(ssl_write_addr, &HookedSSL_write,
                             reinterpret_cast<LPVOID*>(&g_originalSSL_write)) == MH_OK)
            {
                if (MH_EnableHook(ssl_write_addr) == MH_OK)
                {
                    char msg[256];
                    sprintf_s(msg, "[Native Hook] Successfully hooked SSL_write in %s", dllName);
                    SendLog(msg);
                }
            }
        }

        if (ssl_read_addr || ssl_write_addr)
        {
            char msg[256];
            sprintf_s(msg, "[Native Hook] Found OpenSSL in %s", dllName);
            SendLog(msg);
            return; // 成功找到并Hook，退出
        }
    }

    SendLog("[Native Hook] OpenSSL not detected (application may use Schannel)");
}

// 辅助函数：尝试Hook Schannel函数
void TryHookSchannel()
{
    HMODULE hSecur32 = GetModuleHandleA("secur32.dll");
    if (!hSecur32)
        hSecur32 = LoadLibraryA("secur32.dll");

    if (hSecur32)
    {
        void* decrypt_addr = GetProcAddress(hSecur32, "DecryptMessage");

        if (decrypt_addr)
        {
            if (MH_CreateHook(decrypt_addr, &HookedDecryptMessage,
                             reinterpret_cast<LPVOID*>(&g_originalDecryptMessage)) == MH_OK)
            {
                if (MH_EnableHook(decrypt_addr) == MH_OK)
                {
                       SendLog("[Native Hook] Successfully hooked DecryptMessage (Schannel)");
                }
            }
        }
        else
        {
            SendLog("[Native Hook] Schannel DecryptMessage not found");
        }
    }
}

// 初始化 Hook
bool InitializeHooks(const char* pipeName)
{
    if (g_initialized)
        return true;

    // 连接到命名管道
    if (!ConnectToPipe(pipeName))
    {
        return false;
    }

    SendLog("[Native Hook] Initializing MinHook...");

    // 初始化 MinHook
    if (MH_Initialize() != MH_OK)
    {
        SendLog("[Native Hook] Failed to initialize MinHook");
        return false;
    }

    // Hook send
    if (MH_CreateHook(&send, &HookedSend, reinterpret_cast<LPVOID*>(&g_originalSend)) != MH_OK)
    {
        SendLog("[Native Hook] Failed to create hook for send");
    }
    else if (MH_EnableHook(&send) != MH_OK)
    {
        SendLog("[Native Hook] Failed to enable hook for send");
    }
    else
    {
        SendLog("[Native Hook] Successfully hooked send");
    }

    // Hook recv
    if (MH_CreateHook(&recv, &HookedRecv, reinterpret_cast<LPVOID*>(&g_originalRecv)) != MH_OK)
    {
        SendLog("[Native Hook] Failed to create hook for recv");
    }
    else if (MH_EnableHook(&recv) != MH_OK)
    {
        SendLog("[Native Hook] Failed to enable hook for recv");
    }
    else
    {
        SendLog("[Native Hook] Successfully hooked recv");
    }

    // Hook WSASend
    if (MH_CreateHook(&WSASend, &HookedWSASend, reinterpret_cast<LPVOID*>(&g_originalWSASend)) != MH_OK)
    {
        SendLog("[Native Hook] Failed to create hook for WSASend");
    }
    else if (MH_EnableHook(&WSASend) != MH_OK)
    {
        SendLog("[Native Hook] Failed to enable hook for WSASend");
    }
    else
    {
        SendLog("[Native Hook] Successfully hooked WSASend");
    }

    // Hook WSARecv
    if (MH_CreateHook(&WSARecv, &HookedWSARecv, reinterpret_cast<LPVOID*>(&g_originalWSARecv)) != MH_OK)
    {
        SendLog("[Native Hook] Failed to create hook for WSARecv");
    }
    else if (MH_EnableHook(&WSARecv) != MH_OK)
    {
        SendLog("[Native Hook] Failed to enable hook for WSARecv");
    }
    else
    {
        SendLog("[Native Hook] Successfully hooked WSARecv");
    }

    // Hook WinHTTP 函数（用于捕获HTTP/HTTPS数据）
    SendLog("[Native Hook] Attempting to hook WinHTTP functions...");

    HMODULE hWinHttp = GetModuleHandleA("winhttp.dll");
    if (!hWinHttp)
        hWinHttp = LoadLibraryA("winhttp.dll");

    if (hWinHttp)
    {
        void* winHttpReadData = GetProcAddress(hWinHttp, "WinHttpReadData");
        void* winHttpWriteData = GetProcAddress(hWinHttp, "WinHttpWriteData");

        if (winHttpReadData)
        {
            if (MH_CreateHook(winHttpReadData, &HookedWinHttpReadData, reinterpret_cast<LPVOID*>(&g_originalWinHttpReadData)) == MH_OK)
            {
                if (MH_EnableHook(winHttpReadData) == MH_OK)
                {
                    SendLog("[Native Hook] Successfully hooked WinHttpReadData");
                }
            }
        }

        if (winHttpWriteData)
        {
            if (MH_CreateHook(winHttpWriteData, &HookedWinHttpWriteData, reinterpret_cast<LPVOID*>(&g_originalWinHttpWriteData)) == MH_OK)
            {
                if (MH_EnableHook(winHttpWriteData) == MH_OK)
                {
                    SendLog("[Native Hook] Successfully hooked WinHttpWriteData");
                }
            }
        }
    }
    else
    {
        SendLog("[Native Hook] WinHTTP not detected (application may use WinINet or other HTTP library)");
    }

    // Hook WinINet 函数（用于捕获HTTP/HTTPS数据）
    SendLog("[Native Hook] Attempting to hook WinINet functions...");

    HMODULE hWinINet = GetModuleHandleA("wininet.dll");
    if (!hWinINet)
        hWinINet = LoadLibraryA("wininet.dll");

    if (hWinINet)
    {
        void* internetReadFile = GetProcAddress(hWinINet, "InternetReadFile");
        void* internetWriteFile = GetProcAddress(hWinINet, "InternetWriteFile");
        void* httpSendRequestA = GetProcAddress(hWinINet, "HttpSendRequestA");

        if (internetReadFile)
        {
            if (MH_CreateHook(internetReadFile, &HookedInternetReadFile, reinterpret_cast<LPVOID*>(&g_originalInternetReadFile)) == MH_OK)
            {
                if (MH_EnableHook(internetReadFile) == MH_OK)
                {
                    SendLog("[Native Hook] Successfully hooked InternetReadFile");
                }
            }
        }

        if (internetWriteFile)
        {
            if (MH_CreateHook(internetWriteFile, &HookedInternetWriteFile, reinterpret_cast<LPVOID*>(&g_originalInternetWriteFile)) == MH_OK)
            {
                if (MH_EnableHook(internetWriteFile) == MH_OK)
                {
                    SendLog("[Native Hook] Successfully hooked InternetWriteFile");
                }
            }
        }

        if (httpSendRequestA)
        {
            if (MH_CreateHook(httpSendRequestA, &HookedHttpSendRequest, reinterpret_cast<LPVOID*>(&g_originalHttpSendRequest)) == MH_OK)
            {
                if (MH_EnableHook(httpSendRequestA) == MH_OK)
                {
                    SendLog("[Native Hook] Successfully hooked HttpSendRequestA");
                }
            }
        }
    }
    else
    {
        SendLog("[Native Hook] WinINet not detected (application may use other HTTP library)");
    }

    // Hook SSL/TLS 函数（用于解密HTTPS流量）
    SendLog("[Native Hook] Attempting to hook SSL/TLS functions...");
    TryHookOpenSSL();
    TryHookSchannel();

    g_initialized = true;
    SendLog("[Native Hook] All hooks initialized successfully!");

    return true;
}

// 清理 Hook
void UninitializeHooks()
{
    if (!g_initialized)
        return;

    SendLog("[Native Hook] Uninitializing hooks...");

    MH_DisableHook(MH_ALL_HOOKS);
    MH_Uninitialize();

    if (g_hPipe != INVALID_HANDLE_VALUE)
    {
        CloseHandle(g_hPipe);
        g_hPipe = INVALID_HANDLE_VALUE;
    }

    g_initialized = false;
}

// DLL 导出函数：初始化（从 .NET 调用）
extern "C" __declspec(dllexport) BOOL Initialize(const char* pipeName)
{
    return InitializeHooks(pipeName) ? TRUE : FALSE;
}

// DLL 导出函数：清理
extern "C" __declspec(dllexport) void Uninitialize()
{
    UninitializeHooks();
}

// 初始化线程函数
DWORD WINAPI InitThread(LPVOID lpParam)
{
    // 等待主进程启动IPC服务器
    for (int i = 0; i < 10; i++)
    {
        Sleep(500);
        if (InitializeHooks("KuaishouHookPipe"))
        {
            return 0;
        }
    }
    return 1;
}

// DLL 入口点
BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(hModule);
        // 自动初始化 Hook（使用固定的管道名称）
        CreateThread(NULL, 0, InitThread, NULL, 0, NULL);
        break;

    case DLL_PROCESS_DETACH:
        UninitializeHooks();
        break;
    }
    return TRUE;
}
