namespace Glassquid.Dock.Tests;

/// <summary>
/// Chạy phần thân bài kiểm thử trên một luồng STA chuyên biệt.
/// </summary>
/// <remarks>
/// Cửa sổ WPF phải được tạo trên luồng STA, trong khi xunit mặc định chạy các ca kiểm thử trên nhóm luồng (MTA).
/// Ngoại lệ phát sinh trong phần thân kiểm thử được truyền nguyên vẹn về luồng kiểm thử để không làm mất nguyên nhân lỗi.
/// </remarks>
internal static class StaTest
{
    public static void Run(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw failure;
        }
    }
}
