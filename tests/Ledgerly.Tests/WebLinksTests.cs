using Ledgerly.Data;

namespace Ledgerly.Tests;

public class WebLinksTests
{
    [Theory]
    [InlineData("https://www.mybank.com", "https://www.mybank.com/")]
    [InlineData("  http://mybank.com/pay?x=1  ", "http://mybank.com/pay?x=1")]
    [InlineData("mybank.com", "https://mybank.com/")]
    [InlineData("www.mybank.com/login", "https://www.mybank.com/login")]
    [InlineData("mybank.com:8443/pay", "https://mybank.com:8443/pay")]
    [InlineData("HTTPS://MyBank.com/Pay", "https://mybank.com/Pay")]
    [InlineData("http://localhost:5005", "http://localhost:5005/")]
    public void Valid_addresses_are_normalized(string input, string expected)
    {
        Assert.True(WebLinks.TryNormalize(input, out var url));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_means_no_link(string? input)
    {
        Assert.True(WebLinks.TryNormalize(input, out var url));
        Assert.Null(url);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript://%0Aalert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("mailto:me@example.com")]
    [InlineData("ftp://files.mybank.com")]
    [InlineData("file:///C:/secrets.txt")]
    [InlineData("my bank dot com")]
    [InlineData("mybank")]
    [InlineData("https://")]
    public void Unsafe_or_invalid_addresses_are_rejected(string input)
    {
        Assert.False(WebLinks.TryNormalize(input, out var url));
        Assert.Null(url);
    }

    [Fact]
    public void Overly_long_addresses_are_rejected()
    {
        Assert.False(WebLinks.TryNormalize("https://mybank.com/" + new string('a', WebLinks.MaxLength), out _));
    }
}
