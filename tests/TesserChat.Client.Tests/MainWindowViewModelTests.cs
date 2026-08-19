using TesserChat.Client.ViewModels;

namespace TesserChat.Client.Tests;

/// <summary>
/// View models are exercised with no view and no Avalonia app instance — if this ever needs a
/// running UI thread to pass, the view model has grown a view dependency.
/// </summary>
public class MainWindowViewModelTests
{
    [Fact]
    public void Constructs_WithoutAvaloniaApplication()
    {
        var vm = new MainWindowViewModel();

        Assert.NotNull(vm);
    }

    [Fact]
    public void Title_IsProductName()
    {
        var vm = new MainWindowViewModel();

        Assert.Equal("TesserChat", vm.Title);
    }
}
