# TrayNotifyCallbackTest

Công cụ dò tìm Windows x64 gốc (native) dành cho callback riêng `ITrayNotify` của Explorer. Chương trình chạy độc lập, không tham chiếu hay chỉnh sửa Glassquid; chỉ đăng ký/hủy đăng ký `INotificationCB` và đọc dữ liệu callback (không gọi `SetPreference`, không đổi AutoHide và không tắt Explorer).

Chế độ `--run-tests`:

1. Kiểm tra popup khay hệ thống mặc định đang đóng, kích hoạt `Zalo.exe` và ghi nhận callback của Zalo.
2. Tạm thời di chuyển `Shell_TrayWnd` ra ngoài màn hình (không ẩn/hủy cửa sổ) trong lúc một tiến trình nhỏ thêm biểu tượng khay hệ thống, sau đó khôi phục vị trí ban đầu.
3. Gỡ bỏ biểu tượng khay hệ thống của tiến trình thứ hai và kiểm tra callback xóa.

Kích thước/vị trí thanh tác vụ gốc luôn được khôi phục khi kết thúc khối kiểm thử bình thường. Chương trình không bao giờ đóng Zalo. Kết quả được ghi vào `results/TrayNotifyCallbackTest.log`.

Biên dịch từ thư mục này bằng MinGW-w64 x64:

```powershell
g++.exe -std=c++20 -O0 -D_WIN32_WINNT=0x0A00 -static -static-libgcc -static-libstdc++ -municode .\TrayNotifyCallbackTest.cpp -o .\TrayNotifyCallbackTest.exe -lole32 -loleaut32 -lshell32 -luser32 -ladvapi32 -lshlwapi -luuid
.\TrayNotifyCallbackTest.exe --run-tests
```

Để theo dõi thụ động, chạy tệp thực thi không kèm `--run-tests`; nhấn `Ctrl+C` để hủy đăng ký callback và thoát.

`ITrayNotify`, `ITrayNotifyWin8`, `INotificationCB` và `NOTIFYITEM` là các giao diện nội bộ chưa được công khai của Explorer. Công cụ này dùng để kiểm thử trên bản Windows thực tế trước khi tích hợp chính thức.
