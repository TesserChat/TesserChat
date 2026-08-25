using Microsoft.Extensions.Configuration;
using TesserChat.Server.Auth;

namespace TesserChat.Server.Tests.Auth;

/// <summary>
/// Covers how <see cref="AuthOptions"/> binds from configuration (§4.7).
/// </summary>
/// <remarks>
/// Worth testing because the failure is silent: a duration that does not bind leaves the default in
/// place, so an operator who shortened a lifetime would see the server keep the old one with no
/// error anywhere. These run without a database.
/// </remarks>
public sealed class AuthOptionsTests
{
    [Fact]
    public void Durations_BindFromTheFormatTheExampleConfigUses()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Auth:ChallengeLifetime"] = "00:02:00",
            ["Auth:ChallengeRetention"] = "00:15:00",
        });

        Assert.Equal(TimeSpan.FromMinutes(2), options.ChallengeLifetime);
        Assert.Equal(TimeSpan.FromMinutes(15), options.ChallengeRetention);
    }

    [Fact]
    public void Durations_BindFromContainerStyleEnvironmentKeys()
    {
        // What an operator sets on a Compose service — the double-underscore form ASP.NET Core
        // layers over appsettings.json (§5.6).
        var options = Bind(new Dictionary<string, string?>
        {
            ["Auth:ChallengeLifetime"] = "00:00:45",
        });

        Assert.Equal(TimeSpan.FromSeconds(45), options.ChallengeLifetime);

        // Untouched keys keep their defaults rather than resetting to zero.
        Assert.Equal(TimeSpan.FromMinutes(15), options.ChallengeRetention);
    }

    [Fact]
    public void AnEmptySection_LeavesTheDefaults()
    {
        var options = Bind([]);

        Assert.Equal(TimeSpan.FromMinutes(2), options.ChallengeLifetime);
        Assert.Equal(TimeSpan.FromMinutes(15), options.ChallengeRetention);
    }

    private static AuthOptions Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var options = new AuthOptions();
        configuration.GetSection(AuthOptions.SectionName).Bind(options);
        return options;
    }
}
