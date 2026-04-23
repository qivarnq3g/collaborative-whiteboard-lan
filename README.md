# Collaborative Whiteboard LAN

Ứng dụng bảng vẽ cộng tác thời gian thực qua mạng nội bộ (LAN).  
MSSV: 066206005044 — Môn: Lập trình mạng

## Stack

| Thành phần | Công nghệ |
|---|---|
| Server | .NET 10 console, TCP listener, UDP broadcast discovery |
| Client | .NET 10 + WinUI 3 (Windows App SDK), canvas 2D |
| Contracts | Thư viện dùng chung: framing, payload, message type |

## Tính năng

- Khám phá phòng tự động qua UDP broadcast (port 11001)
- Kết nối TCP; đồng bộ nét vẽ, chat, undo/redo theo từng user
- Quản lý phòng: mật khẩu, khóa phòng, kick/ban user, chuyển quyền owner
- Handshake bảo đảm: server gửi `RoomStateInfo` trước snapshot để client nhận state authoritative
- Chặn race condition first-user bằng `ownershipLock` server-side và gate UI-side

## Chạy thử

```
dotnet run --project 066206005044_BTL_WhiteboardServer
```

Chạy client từ Visual Studio hoặc:

```
dotnet run --project 066206005044_BTL_WhiteboardClient
```

> Yêu cầu Windows 10 22621+ và Windows App SDK runtime đã cài.
