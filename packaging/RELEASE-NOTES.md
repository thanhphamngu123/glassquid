# Glassquid 0.2.4 — Gói tệp gốc e3f9f152

Khắc phục thiếu sót đóng gói ở bản 0.2.3: đóng gói trực tiếp bản Glassquid-capture-acceptance-20261007-e3f9f152 do người dùng chỉ định, 258 tệp chương trình, tài liệu hướng dẫn và script có mã băm SHA-256 khớp với thư mục được chỉ định, giữ nguyên số phiên bản nội bộ. Mã nguồn sử dụng bản chụp đầy đủ tương ứng.

Giữ nguyên các hành vi của bản này: thu thập tương thích chụp màn hình, chu kỳ thu thập nền 16ms, giữ nguyên chất liệu khi gọi lên, làm mới khi chuyển cửa sổ và tiến trình nền không xuất hiện trên thanh tác vụ.

Dành riêng cho Windows 11 Build 26100 x64. Mặc định cài vào %LOCALAPPDATA%\Programs\Glassquid-Capture, tách biệt với bản cũ. Cấu hình đặt ở thư mục Data bên cạnh; lần cài đặt đầu tiên đi kèm cấu hình và biểu tượng của gói được chỉ định, các lần cài đặt sau và gỡ cài đặt sẽ giữ lại thư mục Data hiện có. Giữ nguyên hành vi tắt đăng ký khởi động cùng Windows của bản nghiệm thu gốc. Hãy thoát các phiên bản Glassquid khác trước khi chạy.

Xác minh: kiểm tra mã băm tệp gốc, đối chiếu giải nén ZIP, tự kiểm tra ứng dụng, biên dịch toàn bộ mã nguồn và 149 bài kiểm thử Core đều đạt. Kiểm thử hồi quy affinity gốc và cờ WGC màn hình chứng minh bản cũ loại trừ chụp màn hình còn bản mới có thể thu thập. Một đầu dò tham chiếu màn hình nền khác xảy ra sai lệch tham chiếu, đã lưu lại ghi nhận và chưa tính là đạt.

Gói cài đặt chưa được ký; chưa thực hiện cài đặt, nâng cấp, gỡ cài đặt thực tế cũng như nghiệm thu thủ công với QQ, WeChat, OBS.
