// Admin.NET 项目的版权、商标、专利和其他相关权利均受相应法律法规的保护。使用本项目应遵守相关法律法规和许可证的要求。
//
// 本项目主要遵循 MIT 许可证和 Apache 许可证（版本 2.0）进行分发和使用。许可证位于源代码树根目录中的 LICENSE-MIT 和 LICENSE-APACHE 文件。
//
// 不得利用本项目从事危害国家安全、扰乱社会秩序、侵犯他人合法权益等法律法规禁止的活动！任何基于本项目二次开发而产生的一切法律纠纷和责任，我们不承担任何责任！

using OpenQA.Selenium;
using Xunit;

namespace Admin.NET.Test.User;

public class UserTest : BaseTest
{
    // 用户登录 token
    private static readonly string Token = "xxxxxxxxx";

    /// <summary>初始化 UserTest 实例，保存后续处理所需的依赖并建立初始状态。</summary>
    public UserTest() : base(Token)
    {
    }

    /// <summary>调用 UserTest 的异步依赖完成 Login 操作，并按当前返回类型传递处理结果。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task Login()
    {
        await base.Login();
        WaitEnter();
    }

    /// <summary>构建 UserTest 所需的 Add User 内容，并保证生成结果符合调用约定。</summary>
    /// <returns>返回值与当前方法声明的类型及空值约束保持一致。</returns>
    [Fact]
    public async Task AddUser()
    {
        await Task.Delay(1000);

        await GoToUrlAsync("/#/system/user");
        var addBut = Driver.FindElement(By.XPath("//*[@id=\"app\"]/section/section/div/div[1]/div/main/div/div[1]/div/div[1]/div[1]/div[1]/div[3]/div[1]/div/form/div[6]/div/button"));
        addBut.Click();

        //点击基础信息选项卡
        await Task.Delay(1000);
        Driver.FindElement(By.Id("tab-0")).Click();

        var tab = Driver.FindElement(By.Id("pane-0"));
        var formItemList = tab.FindElements(By.CssSelector("input"));

        // 输入 账号名称
        var first = formItemList.First();
        first.Clear();
        first.SendKeys("test1");
        await Task.Delay(1000);

        // 输入 手机号码
        var second = formItemList.Skip(1).First();
        second.Clear();
        second.SendKeys("17396157893");
        await Task.Delay(1000);

        // 输入 姓名
        var third = formItemList.Skip(2).First();
        third.Clear();
        third.SendKeys("测试1");
        await Task.Delay(1000);

        // 阻塞
        WaitEnter();
    }
}