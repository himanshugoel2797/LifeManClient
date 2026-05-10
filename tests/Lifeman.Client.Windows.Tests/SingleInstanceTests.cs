using Lifeman.Client.Windows;

namespace Lifeman.Client.Windows.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void First_Acquires_Second_Blocked_Then_Released()
    {
        using var a = SingleInstance.TryAcquire();
        Assert.True(a.Acquired);

        using (var b = SingleInstance.TryAcquire())
        {
            Assert.False(b.Acquired);
        }

        // Release first holder, then a new acquire should succeed.
        a.Dispose();
        using var c = SingleInstance.TryAcquire();
        Assert.True(c.Acquired);
    }
}
