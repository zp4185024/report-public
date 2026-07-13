// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using Admin.NET.Core;
using Xunit;

namespace Admin.NET.Test.Utils;

public class DateTimeUtilTests
{
    [Fact]
    public void Init_WithTimeSpan_ReturnsCorrectDateTime()
    {
        // Arrange
        var timeSpan = new TimeSpan(1, 0, 0, 0); // 1天

        // Act
        var dateTimeUtil = DateTimeUtil.Init(timeSpan);

        // Assert
        Assert.Equal(DateTime.Now.AddDays(1).Date, dateTimeUtil.Date.Date);
    }

    [Fact]
    public void Init_WithDateTime_ReturnsCorrectDateTime()
    {
        // Arrange
        var dateTime = new DateTime(2023, 10, 1);

        // Act
        var dateTimeUtil = DateTimeUtil.Init(dateTime);

        // Assert
        Assert.Equal(dateTime, dateTimeUtil.Date);
    }

    [Fact]
    public void GetTodayRange_ReturnsCorrectRange()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15, 12, 30, 0));

        // Act
        var (start, end) = dateTimeUtil.GetTodayRange();

        // Assert
        Assert.Equal(new DateTime(2023, 10, 15), start); // 当天开始时间
        Assert.Equal(new DateTime(2023, 10, 15, 23, 59, 59), end); // 当天结束时间
    }

    [Fact]
    public void GetMonthRange_ReturnsCorrectRange()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15));

        // Act
        var (start, end) = dateTimeUtil.GetMonthRange();

        // Assert
        Assert.Equal(new DateTime(2023, 10, 1), start); // 本月第一天
        Assert.Equal(new DateTime(2023, 10, 31, 23, 59, 59), end); // 本月最后一天
    }

    [Fact]
    public void GetFirstDayOfMonth_ReturnsCorrectDate()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15));

        // Act
        var firstDay = dateTimeUtil.GetFirstDayOfMonth();

        // Assert
        Assert.Equal(new DateTime(2023, 10, 1), firstDay); // 本月第一天
    }

    [Fact]
    public void GetLastDayOfMonth_ReturnsCorrectDate()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15));

        // Act
        var lastDay = dateTimeUtil.GetLastDayOfMonth();

        // Assert
        Assert.Equal(new DateTime(2023, 10, 31, 23, 59, 59), lastDay); // 本月最后一天
    }

    [Fact]
    public void GetYearRange_ReturnsCorrectRange()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15));

        // Act
        var (start, end) = dateTimeUtil.GetYearRange();

        // Assert
        Assert.Equal(new DateTime(2023, 1, 1), start); // 今年第一天
        Assert.Equal(new DateTime(2023, 12, 31, 23, 59, 59), end); // 今年最后一天
    }

    [Fact]
    public void GetFirstDayOfYear_ReturnsCorrectDate()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15));

        // Act
        var firstDay = dateTimeUtil.GetFirstDayOfYear();

        // Assert
        Assert.Equal(new DateTime(2023, 1, 1), firstDay); // 今年第一天
    }

    [Fact]
    public void GetLastDayOfYear_ReturnsCorrectDate()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15));

        // Act
        var lastDay = dateTimeUtil.GetLastDayOfYear();

        // Assert
        Assert.Equal(new DateTime(2023, 12, 31, 23, 59, 59), lastDay); // 今年最后一天
    }

    [Fact]
    public void GetDayBeforeYesterdayRange_ReturnsCorrectRange()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15));

        // Act
        var (start, end) = dateTimeUtil.GetDayBeforeYesterdayRange();

        // Assert
        Assert.Equal(new DateTime(2023, 10, 13), start); // 前天开始时间
        Assert.Equal(new DateTime(2023, 10, 13, 23, 59, 59), end); // 前天结束时间
    }

    [Fact]
    public void GetYesterdayRange_ReturnsCorrectRange()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15));

        // Act
        var (start, end) = dateTimeUtil.GetYesterdayRange();

        // Assert
        Assert.Equal(new DateTime(2023, 10, 14), start); // 昨天开始时间
        Assert.Equal(new DateTime(2023, 10, 14, 23, 59, 59), end); // 昨天结束时间
    }

    [Fact]
    public void GetLastWeekRange_ReturnsCorrectRange()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15)); // 2023-10-15 是周日

        // Act
        var (start, end) = dateTimeUtil.GetLastWeekRange();

        // Assert
        Assert.Equal(new DateTime(2023, 10, 8), start); // 上周第一天（周一）
        Assert.Equal(new DateTime(2023, 10, 14, 23, 59, 59), end); // 上周最后一天（周日）
    }

    [Fact]
    public void GetThisWeekRange_ReturnsCorrectRange()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15)); // 2023-10-15 是周日

        // Act
        var (start, end) = dateTimeUtil.GetThisWeekRange();

        // Assert
        Assert.Equal(new DateTime(2023, 10, 15), start); // 本周第一天（周一）
        Assert.Equal(new DateTime(2023, 10, 21, 23, 59, 59), end); // 本周最后一天（周日）
    }

    [Fact]
    public void GetLastMonthRange_ReturnsCorrectRange()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15));

        // Act
        var (start, end) = dateTimeUtil.GetLastMonthRange();

        // Assert
        Assert.Equal(new DateTime(2023, 9, 1), start); // 上月第一天
        Assert.Equal(new DateTime(2023, 9, 30, 23, 59, 59), end); // 上月最后一天
    }

    [Fact]
    public void GetLast3DaysRange_ReturnsCorrectRange()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15));

        // Act
        var (start, end) = dateTimeUtil.GetLast3DaysRange();

        // Assert
        Assert.Equal(new DateTime(2023, 10, 13), start); // 3天前的开始时间
        Assert.Equal(new DateTime(2023, 10, 15, 23, 59, 59), end); // 当前日期的结束时间
    }

    [Fact]
    public void GetLast7DaysRange_ReturnsCorrectRange()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15));

        // Act
        var (start, end) = dateTimeUtil.GetLast7DaysRange();

        // Assert
        Assert.Equal(new DateTime(2023, 10, 9), start); // 7天前的开始时间
        Assert.Equal(new DateTime(2023, 10, 15, 23, 59, 59), end); // 当前日期的结束时间
    }

    [Fact]
    public void GetLast15DaysRange_ReturnsCorrectRange()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15));

        // Act
        var (start, end) = dateTimeUtil.GetLast15DaysRange();

        // Assert
        Assert.Equal(new DateTime(2023, 10, 1), start); // 15天前的开始时间
        Assert.Equal(new DateTime(2023, 10, 15, 23, 59, 59), end); // 当前日期的结束时间
    }

    [Fact]
    public void GetLast3MonthsRange_ReturnsCorrectRange()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15));

        // Act
        var (start, end) = dateTimeUtil.GetLast3MonthsRange();

        // Assert
        Assert.Equal(new DateTime(2023, 7, 15), start); // 3个月前的开始时间
        Assert.Equal(new DateTime(2023, 10, 15, 23, 59, 59), end); // 当前日期的结束时间
    }

    [Fact]
    public void GetFirstHalfYearRange_ReturnsCorrectRange()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15));

        // Act
        var (start, end) = dateTimeUtil.GetFirstHalfYearRange();

        // Assert
        Assert.Equal(new DateTime(2023, 1, 1), start); // 上半年开始时间
        Assert.Equal(new DateTime(2023, 6, 30, 23, 59, 59), end); // 上半年结束时间
    }

    [Fact]
    public void GetSecondHalfYearRange_ReturnsCorrectRange()
    {
        // Arrange
        var dateTimeUtil = DateTimeUtil.Init(new DateTime(2023, 10, 15));

        // Act
        var (start, end) = dateTimeUtil.GetSecondHalfYearRange();

        // Assert
        Assert.Equal(new DateTime(2023, 7, 1), start); // 下半年开始时间
        Assert.Equal(new DateTime(2023, 12, 31, 23, 59, 59), end); // 下半年结束时间
    }
}