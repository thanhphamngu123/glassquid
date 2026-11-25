# Glassquid 1.0.0

Công cụ tùy biến giao diện màn hình nền Windows 11 x64 với phong cách kính mờ: thanh Dock, Launchpad, thư mục ứng dụng và trung tâm Cài đặt thống nhất (giao diện tiếng Việt).

- **Repository**: https://github.com/thanhphamngu123/glassquid
- **Releases**: https://github.com/thanhphamngu123/glassquid/releases
- **Tài liệu liên quan**: [Ghi chú phát hành](docs/Release-notes-v1.0.0.md) · [Hướng dẫn đóng góp](CONTRIBUTING.md) · [Phạm vi bản quyền](docs/LICENSING.md)

## Tính năng chính

- **Dock kính mờ**: Thanh Dock hiển thị ứng dụng ghim và ứng dụng đang chạy, hiệu ứng phóng đại khi rê chuột, hỗ trợ tự động ẩn/hiện, khay hệ thống và nút Launchpad / Desktop / This PC.
- **Launchpad & Thư mục**: Tự động quét ứng dụng, kéo thả sắp xếp, gom nhóm thành thư mục (đổi tên, sắp xếp trong thư mục, tự giải tán khi còn 1 ứng dụng), tìm kiếm nhanh, tự động dọn hoặc xóa thủ công các ứng dụng đã gỡ.
- **Trung tâm Cài đặt**: Tùy chỉnh giao diện, kích thước Dock/Launchpad, thao tác nhanh, hiệu suất, ẩn/hiện taskbar Windows, bật/tắt tự động khởi động cùng Windows và kiểm tra cập nhật tự động qua GitHub (`thanhphamngu123/glassquid`).

## Yêu cầu & Biên dịch (Build / Run)

Yêu cầu Windows 11 x64 (Build 22000 trở lên) và .NET 10 SDK (`10.0.401`, Windows SDK `10.0.26100.0`).

```powershell
dotnet build src/Glassquid.App/Glassquid.App.csproj -c Release
dotnet run --project src/Glassquid.App/Glassquid.App.csproj -c Release
```

Ứng dụng lưu thư mục `Data` ngay cạnh thư mục chạy. Trước khi chạy bản build phát triển, hãy thoát phiên bản `Glassquid` đang chạy ở khay hệ thống (nếu có).

```powershell
dotnet test tests/Glassquid.Core.Tests/Glassquid.Core.Tests.csproj -c Release
dotnet test tests/Glassquid.AppServices.Tests/Glassquid.AppServices.Tests.csproj -c Release
dotnet test tests/Glassquid.Dock.Tests/Glassquid.Dock.Tests.csproj -c Release
```

## Đóng gói bản Portable & Bộ cài đặt (Installer)

```powershell
dotnet publish src/Glassquid.App/Glassquid.App.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:InformationalVersion=1.0.0 -p:IncludeSourceRevisionInInformationalVersion=false -o artifacts/publish
Copy-Item LICENSE artifacts/publish/LICENSE.txt
Copy-Item NOTICE artifacts/publish/NOTICE.txt
Copy-Item docs/third-party artifacts/publish/third-party -Recurse
Copy-Item docs/LICENSING.md artifacts/publish/LICENSING.md
Copy-Item docs/licensing artifacts/publish/licensing -Recurse
```

Tệp `artifacts/publish/Glassquid.exe` có thể chạy trực tiếp. Không đưa thư mục `Data` cá nhân vào gói phân phối và giữ nguyên các tệp bản quyền đi kèm.

Để tạo bộ cài `.exe` bằng Inno Setup 7 (với `ISCC.exe` trong `PATH`):

```powershell
$env:GLASSQUID_PACKAGE_STAGE = (Resolve-Path artifacts/publish).Path
ISCC /DAppIdValue=Glassquid.Independent /DProductVersion=1.0.0 /DFileVersion=1.0.0.0 /DPackageBaseName=Glassquid-1.0.0-win-x64 /Oartifacts packaging/Glassquid-Capture.iss
```

## Bản quyền

- Mã nguồn gốc và các thành phần bên thứ ba giữ nguyên giấy phép tương ứng (Apache-2.0, Unicode-3.0, xem [LICENSE](LICENSE), [NOTICE](NOTICE), [docs/LICENSING.md](docs/LICENSING.md) và [docs/third-party](docs/third-party)).
- Các phần bổ sung/chỉnh sửa thuộc phạm vi **PolyForm Noncommercial License 1.0.0** (cho phép sử dụng, học tập, chỉnh sửa và chia sẻ phi thương mại).
