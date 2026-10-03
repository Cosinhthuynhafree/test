# HoshinoTransfer

Ứng dụng truyền tệp và trò chuyện native giữa iPhone và Windows. Monorepo gồm client WPF .NET 8, client SwiftUI iOS và API Node.js 22 không phụ thuộc npm package. Endpoint public mặc định là `https://rt2ucj.taild7fb6f.ts.net/`; server mặc định lắng nghe port `3000`. Host public phải được chủ hệ thống định tuyến qua Tailscale Funnel/reverse proxy; source này không cấu hình dịch vụ mạng bên ngoài đó.

> **Trạng thái bàn giao:** workspace ban đầu chỉ có `git-token.txt` cục bộ. Tài liệu này phân biệt rõ phần đã có mã với các transport chưa được triển khai. Xem `todo.md`. Không khẳng định đã có Wi-Fi Direct, P2P NAT traversal hoặc Lightning.

## Architecture

```text
Windows (WPF / .NET 8) ─┐
                         ├── HTTPS REST + SSE có xác thực ── API :3000 ── SQLite
iPhone (SwiftUI / iOS) ──┘                                      └── lưu relay theo chunk
```

- `Server/HoshinoTransfer.Server/`: HTTP API dùng Node.js 22 built-in, SQLite, mật khẩu băm scrypt, phiên opaque có hạn dùng, API v1 và integration tests. SQLite là persistence ban đầu; chưa có PostgreSQL.
- `Windows/HoshinoTransfer.Windows/`: ứng dụng desktop WPF native; không dùng Electron, WebView2, WinForms hay runtime UI bên thứ ba.
- `iOS/HoshinoTransfer.iOS/`: ứng dụng SwiftUI native và Xcode project.
- `.github/workflows/ios-build.yml`: macOS runner build app không ký, đóng gói thành `HoshinoTransfer.ipa` và upload Actions artifact. Không tuyên bố chữ ký Apple hay phân phối App Store.
- `Shared/`: hợp đồng API/transport dùng chung dưới dạng tài liệu; clients vẫn native.

Production API root mặc định là `https://rt2ucj.taild7fb6f.ts.net/`. Có thể cấu hình port/thư mục dữ liệu server và endpoint client trong development. URL production không cho người dùng thông thường sửa trong giao diện.

## Requirements

- Server: Node.js 22.5+ (có `node:sqlite` built-in), Windows/Linux/macOS.
- Windows: Windows 10/11 và .NET 8 SDK/Desktop Runtime. Build trên Windows.
- iOS: Xcode trên macOS để build local hoặc GitHub Actions `macos-latest`.
- Public server: port 3000 được TLS reverse proxy/Funnel cấu hình đúng. Không mở trực tiếp Node HTTP ra Internet nếu chưa có TLS termination và firewall.

## Server setup

```powershell
cd Server/HoshinoTransfer.Server
$env:PORT = "3000"
$env:HOST = "0.0.0.0"
node server.js
```

SQLite và dữ liệu tệp nhận được mặc định lưu trong `data/` và `uploads/`. Production nên đặt `HOSHINO_DATA_DIR` và `HOSHINO_UPLOAD_DIR` thành thư mục bền vững, chỉ service account mới truy cập được và có backup. Đặt reverse proxy trước service để phục vụ HTTPS tại endpoint production và chuyển tiếp về port 3000. Không có mật khẩu DB hard-code.

Health check: `GET /api/health` hoặc `GET /api/v1/health`. Tài liệu development: `GET /api/docs`; OpenAPI 3 JSON: `GET /api/openapi.json`. CORS bị từ chối mặc định; chỉ bật cho các origin khớp chính xác trong `HOSHINO_ALLOWED_ORIGINS` (danh sách phân cách dấu phẩy). Native clients không cần CORS.

## Database setup

Schema SQLite cơ sở idempotent được áp dụng lúc khởi động. Chưa có migration versioned tracking. Integration tests đặt `HOSHINO_TEST_MEMORY=1` để dùng DB in-memory tách biệt; payload tệp dùng thư mục tạm. Khi chạy thông thường, server dùng file SQLite trong `data/`. Cần kiểm tra lại khởi động với DB file trên host triển khai trước khi publish. PostgreSQL chưa được kết nối; SQLite hiện tại cũng chưa hỗ trợ nhiều instance. Trước khi mở rộng production cần thêm migrations và PostgreSQL repository rồi kiểm thử chúng.

## Windows setup

```powershell
dotnet build Windows/HoshinoTransfer.Windows/HoshinoTransfer.Windows.csproj -c Release
dotnet run --project Windows/HoshinoTransfer.Windows/HoshinoTransfer.Windows.csproj
```

Có thể publish executable phụ thuộc .NET Runtime:

```powershell
dotnet publish Windows/HoshinoTransfer.Windows/HoshinoTransfer.Windows.csproj -c Release -r win-x64 --self-contained false -o dist/windows
```

Client dùng endpoint production theo mặc định. Chỉ build Debug mới đọc biến `HOSHINOTRANSFER_API_URL` để development/test. Self-test: `HoshinoTransfer.Windows.exe --self-test`.

## iOS setup

Mở `iOS/HoshinoTransfer.iOS/HoshinoTransfer.xcodeproj` bằng Xcode, chọn scheme và iOS device/simulator rồi build. Target dùng SwiftUI và system networking/files APIs. Thiết kế đăng nhập sản phẩm không phụ thuộc App Store receipt, StoreKit, App Store Connect hoặc Apple ID.

## GitHub Actions and unsigned IPA

Đưa repository lên GitHub rồi chạy **iOS Unsigned IPA** trong Actions (hoặc push nhánh đã cấu hình). Workflow dùng macOS/Xcode runner, tắt code signing, đóng gói `Payload/HoshinoTransfer.app` thành `HoshinoTransfer.ipa`, rồi upload artifact. Không cần certificate/provisioning profile phân phối và workflow không tuyên bố IPA đã ký. Không cần Apple signing secrets. IPA không được commit vào repository.

## LiveContainer Installation

1. Tải `HoshinoTransfer.ipa` từ GitHub Actions Artifact.
2. Chuyển IPA vào iPhone.
3. Import IPA bằng LiveContainer.
4. Mở HoshinoTransfer.
5. Đăng ký hoặc đăng nhập.
6. Pair với Windows khi luồng pairing được triển khai.

Khả năng LiveContainer tùy phiên bản iOS và cấu hình host. Nếu iOS giới hạn một capability, ứng dụng cần phát hiện và báo rõ; không giả lập thành công.

## Environment variables

| Biến | Công dụng | Mặc định |
|---|---|---|
| `PORT` | Port API | `3000` |
| `HOST` | Địa chỉ bind | `0.0.0.0` |
| `HOSHINO_DATA_DIR` | Thư mục DB SQLite | `Server/HoshinoTransfer.Server/data` |
| `HOSHINO_UPLOAD_DIR` | Chunk và tệp relay hoàn chỉnh | `Server/HoshinoTransfer.Server/uploads` |
| `HOSHINO_ALLOWED_ORIGINS` | Origin trình duyệt chính xác được CORS cho phép, phân cách dấu phẩy | rỗng (từ chối) |
| `HOSHINOTRANSFER_API_URL` | Override endpoint trong development; không đưa thành tuỳ chọn production | `https://rt2ucj.taild7fb6f.ts.net/` |

## Security

- Mật khẩu được băm bằng scrypt, không lưu plaintext.
- Access/refresh credential là token opaque ngẫu nhiên; chỉ hash token được lưu. Token có thời hạn và refresh sẽ rotate token.
- Private routes yêu cầu session hợp lệ và kiểm tra sender/receiver. Request body, tên tệp, kích thước chunk, metadata được kiểm tra. Tệp nhận dùng đường dẫn do server tạo, không dùng đường dẫn client gửi.
- Server có rate limiting, security headers, hạn transfer và xác minh SHA-256.
- Đặt HTTP service sau TLS termination, chỉ cho proxy vào port 3000, bảo vệ thư mục SQLite/uploads. Không commit `git-token.txt`, token, database hay logs.

## Development and tests

```powershell
cd Server/HoshinoTransfer.Server
node --test
```

```powershell
dotnet build Windows/HoshinoTransfer.Windows/HoshinoTransfer.Windows.csproj -c Release
```

Trên macOS có thể chạy lại các bước Xcode build/package tương ứng trong `.github/workflows/ios-build.yml`. Không thể build iOS/Xcode local trên Windows.

## Production deployment

Chạy API như service được quản lý sau TLS endpoint public. Lưu bền vững và backup DB/uploads; cấu hình giới hạn tài nguyên/firewall, theo dõi dung lượng và thử quy trình khôi phục/rotate. Một SQLite instance và relay server phù hợp quy mô phát triển, không phải đảm bảo khả năng mở rộng ngang. Transport Wi-Fi Direct/P2P/Lightning và các flow chấp nhận xuyên suốt hai client còn mở trong `todo.md`.

## Troubleshooting

- **Port đang được dùng:** đặt `PORT` khác trong development và đồng bộ với endpoint override của client.
- **Health check không truy cập được:** kiểm tra tiến trình Node, reverse proxy tới port 3000 và trạng thái TLS/Funnel.
- **Lỗi database:** kiểm tra thư mục dữ liệu tồn tại và service account có quyền ghi.
- **IPA không import được:** xác nhận tải đúng artifact sideload không ký; khả năng tương thích LiveContainer phụ thuộc phiên bản/cấu hình iOS. Workflow không ký App Store.
- **Không build được iOS trên Windows:** đây là giới hạn nền tảng; dùng GitHub Actions macOS runner hoặc Mac có Xcode.
- **Transport:** chỉ relay đã triển khai mới hiển thị là `Server Relay`; không gắn nhãn P2P, Wi-Fi Direct hay Lightning.
