# Mã QR nhận tài trợ

Sử dụng ảnh gốc mã QR nhận tiền thông thường do tác giả cung cấp (không đặt sẵn số tiền cố định), giữ nguyên tệp gốc, không vẽ lại mã QR:

- `alipay.jpg`: Mã nhận tiền Alipay (cũng tương thích `alipay.png`).
- `wechat.png`: Mã nhận tiền WeChat.

Hình ảnh được đóng gói dưới dạng tài nguyên nhúng của thành phần Settings và phát hành kèm phần mềm. Trang cài đặt chỉ hiển thị lựa chọn phương thức thanh toán và nút "Tài trợ"; sau khi nhấn nút, hộp thoại riêng mới hiển thị mã nhận tiền của kênh đã chọn. Khi thiếu hoặc không thể đọc được tệp, hộp thoại sẽ hiển thị kênh này tạm thời chưa mở, không hiển thị mã QR của kênh khác và không ảnh hưởng đến các tính năng miễn phí.

Người tài trợ tự nhập số tiền tùy ý trong ứng dụng thanh toán trên điện thoại. Phần mềm không tạo đơn hàng thanh toán và không kiểm tra trạng thái tiền vào tài khoản; kết quả thanh toán lấy theo Alipay hoặc WeChat.
