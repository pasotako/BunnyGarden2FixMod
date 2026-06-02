using UnityEngine;
using Xunit;

public class SmokeTest
{
    [Fact]
    public void Vector3_Arithmetic_Works_At_Runtime()
    {
        var a = new Vector3(1f, 2f, 3f);
        var b = new Vector3(4f, 5f, 6f);
        var sum = a + b;
        Assert.Equal(5f, sum.x, 5);
        Assert.Equal(7f, sum.y, 5);
        Assert.Equal(9f, sum.z, 5);
        Assert.Equal(5f, Mathf.Sqrt(25f), 5);
    }
}
