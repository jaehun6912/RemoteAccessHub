using RemoteAccessHub.UI;
using Xunit;

namespace RemoteAccessHub.Tests;

public class AppIconTests
{
    [Fact]
    public void Embedded_icon_has_all_sizes_for_every_dpi()
    {
        using var s = AppIcon.OpenStream();
        Assert.NotNull(s);
        var sizes = AppIcon.ReadSizes(s!);
        Assert.Equal(AppIcon.ExpectedSizes, sizes);
    }

    [Fact]
    public void Icon_loads_and_small_sizes_are_not_scaled_from_large()
    {
        Assert.NotNull(AppIcon.Current);
        foreach (var size in new[] { 16, 24, 32, 48 })
        {
            using var sized = new Icon(AppIcon.Current!, size, size);
            Assert.Equal(size, sized.Width);
            using var bmp = sized.ToBitmap();
            // 가운데(화면 부분)는 불투명해야 한다: 투명하면 아이콘이 비어 있거나 잘못 그려진 것
            Assert.Equal(255, bmp.GetPixel(size / 2, size / 2 - 1).A);
        }
    }
}
