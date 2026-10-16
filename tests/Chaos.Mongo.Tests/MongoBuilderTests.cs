// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests;

using Chaos.Mongo.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using NUnit.Framework;

public class MongoBuilderTests
{
    [Test]
    public void ConfigureOptions_AfterAddMongoWithConfiguration_OverridesBoundValues()
    {
        // Arrange
        IConfiguration configuration = new ConfigurationBuilder()
                                       .AddInMemoryCollection(new Dictionary<String, String?>
                                       {
                                           ["Mongo:Url"] = "mongodb://localhost:27017/confdb",
                                           ["Mongo:DefaultDatabase"] = "boundDb"
                                       })
                                       .Build();
        var services = new ServiceCollection();
        String? observedDefaultDatabase = null;

        // Act
        services.AddMongo(configuration, "Mongo")
                .ConfigureOptions((_, options) =>
                {
                    observedDefaultDatabase = options.DefaultDatabase;
                    options.DefaultDatabase = "overriddenDb";
                });

        // Assert
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<MongoOptions>>().Value;
        observedDefaultDatabase.Should().Be("boundDb");
        options.DefaultDatabase.Should().Be("overriddenDb");
        options.Url.Should().NotBeNull();
        options.Url!.DatabaseName.Should().Be("confdb");
    }

    [Test]
    public void ConfigureOptions_AfterAddMongoWithConfigureAction_RunsAfterConfigureAction()
    {
        // Arrange
        var services = new ServiceCollection();
        String? observedHolderId = null;

        // Act
        services.AddMongo(options =>
                {
                    options.Url = new MongoUrl("mongodb://localhost:27017/test");
                    options.HolderId = "fromConfigureAction";
                })
                .ConfigureOptions((_, options) => observedHolderId = options.HolderId);

        // Assert
        _ = services.BuildServiceProvider().GetRequiredService<IOptions<MongoOptions>>().Value;
        observedHolderId.Should().Be("fromConfigureAction");
    }

    [Test]
    public void ConfigureOptions_ReturnsBuilderForChaining()
    {
        // Arrange
        var builder = new MongoBuilder(new ServiceCollection());

        // Act
        var result = builder.ConfigureOptions((_, _) => { });

        // Assert
        result.Should().BeSameAs(builder);
    }

    [Test]
    public void ConfigureOptions_WhenCalledMultipleTimes_RunsInRegistrationOrder()
    {
        // Arrange
        var services = new ServiceCollection();
        var invocations = new List<String>();

        // Act
        services.AddMongo("mongodb://localhost:27017/test")
                .ConfigureOptions((_, _) => invocations.Add("first"))
                .ConfigureOptions((_, _) => invocations.Add("second"));

        // Assert
        _ = services.BuildServiceProvider().GetRequiredService<IOptions<MongoOptions>>().Value;
        invocations.Should().Equal("first", "second");
    }

    [Test]
    public void ConfigureOptions_WhenConfigureIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var builder = new MongoBuilder(new ServiceCollection());

        // Act
        var act = () => builder.ConfigureOptions(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
           .WithParameterName("configure");
    }

    [Test]
    public void ConfigureOptions_WhenUrlIsMissing_FailsValidation()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMongo()
                .ConfigureOptions((_, options) => options.UseDefaultCollectionNames = false);
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<MongoOptions>>();

        // Act
        var act = () => _ = options.Value;

        // Assert
        act.Should().Throw<OptionsValidationException>()
           .WithMessage("*MongoOptions.Url must be configured*");
    }

    [Test]
    public void ConfigureOptions_WhenUrlIsSuppliedFromRegisteredService_PassesValidation()
    {
        // Arrange
        var url = new MongoUrl("mongodb://localhost:27017/fromService");
        var services = new ServiceCollection();
        services.AddSingleton(url);

        // Act
        services.AddMongo()
                .ConfigureOptions((serviceProvider, options) => options.Url = serviceProvider.GetRequiredService<MongoUrl>());

        // Assert
        var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IStartupValidator>().Validate();
        act.Should().NotThrow();
        provider.GetRequiredService<IOptions<MongoOptions>>().Value.Url.Should().BeSameAs(url);
    }

    [Test]
    public void Constructor_WhenServicesIsNull_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new MongoBuilder(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
           .WithParameterName("services");
    }

    [Test]
    public void Constructor_WithValidServices_InitializesSuccessfully()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var builder = new MongoBuilder(services);

        // Assert
        builder.Should().NotBeNull();
        builder.Services.Should().BeSameAs(services);
        builder.DiscoveredConfigurators.Should().BeEmpty();
    }

    [Test]
    public void DiscoveredConfigurators_AfterAutoDiscovery_ContainsDiscoveredTypes()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);
        var testAssembly = typeof(MongoBuilderTests).Assembly;

        // Act
        builder.WithConfiguratorAutoDiscovery([testAssembly]);

        // Assert
        builder.DiscoveredConfigurators.Should().NotBeEmpty();
        builder.DiscoveredConfigurators.Should().Contain(typeof(TestConfigurator));
    }

    [Test]
    public void DiscoveredConfigurators_BeforeAutoDiscovery_IsEmpty()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);

        // Act
        var result = builder.DiscoveredConfigurators;

        // Assert
        result.Should().BeEmpty();
    }

    [Test]
    public void FluentChaining_WithMultipleOperations_WorksCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var result = new MongoBuilder(services)
                     .WithConfigurator<TestConfigurator>()
                     .WithConfiguratorAutoDiscovery([typeof(MongoBuilderTests).Assembly])
                     .Services;

        // Assert
        result.Should().BeSameAs(services);
        var provider = services.BuildServiceProvider();
        var configurators = provider.GetServices<IMongoConfigurator>().ToList();
        configurators.Should().NotBeEmpty();
    }

    [Test]
    public void Services_ReturnsUnderlyingServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);

        // Act
        var result = builder.Services;

        // Assert
        result.Should().BeSameAs(services);
    }

    [Test]
    public void WithConfiguratorAutoDiscovery_PreventsDuplicateRegistrations()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);
        var testAssembly = typeof(MongoBuilderTests).Assembly;

        // Act
        builder.WithConfiguratorAutoDiscovery([testAssembly]);
        builder.WithConfiguratorAutoDiscovery([testAssembly]);

        // Assert
        var provider = services.BuildServiceProvider();
        var configurators = provider.GetServices<IMongoConfigurator>().ToList();
        var testConfiguratorCount = configurators.Count(c => c is TestConfigurator);

        // Should only be registered once despite calling auto-discovery twice
        testConfiguratorCount.Should().Be(1);
    }

    [Test]
    public void WithConfiguratorAutoDiscovery_RegistersDiscoveredConfigurators()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);
        var testAssembly = typeof(MongoBuilderTests).Assembly;

        // Act
        builder.WithConfiguratorAutoDiscovery([testAssembly]);

        // Assert
        var provider = services.BuildServiceProvider();
        var configurators = provider.GetServices<IMongoConfigurator>().ToList();
        configurators.Should().NotBeEmpty();
        configurators.Should().Contain(c => c is TestConfigurator);
    }

    [Test]
    public void WithConfiguratorAutoDiscovery_ReturnsBuilderForChaining()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);

        // Act
        var result = builder.WithConfiguratorAutoDiscovery([typeof(MongoBuilderTests).Assembly]);

        // Assert
        result.Should().BeSameAs(builder);
    }

    [Test]
    public void WithConfiguratorAutoDiscovery_WhenCalledMultipleTimes_AccumulatesDiscoveredTypes()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);
        var testAssembly = typeof(MongoBuilderTests).Assembly;

        // Act
        builder.WithConfiguratorAutoDiscovery([testAssembly]);
        var firstCount = builder.DiscoveredConfigurators.Length;
        builder.WithConfiguratorAutoDiscovery([testAssembly]);
        var secondCount = builder.DiscoveredConfigurators.Length;

        // Assert
        // Second call should not increase count due to duplicate prevention
        secondCount.Should().Be(firstCount);
    }

    [Test]
    public void WithConfiguratorAutoDiscovery_WhenNoAssembliesProvided_ScansCallingAssembly()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);

        // Act
        builder.WithConfiguratorAutoDiscovery();

        // Assert
        builder.DiscoveredConfigurators.Should().Contain(typeof(TestConfigurator));
        builder.DiscoveredConfigurators.Should().Contain(typeof(AnotherTestConfigurator));
    }

    [Test]
    public void WithConfiguratorAutoDiscovery_WithEmptyAssemblyList_ScansCallingAssembly()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);

        // Act
        builder.WithConfiguratorAutoDiscovery([]);

        // Assert
        builder.DiscoveredConfigurators.Should().NotBeEmpty();
    }

    [Test]
    public void WithConfiguratorAutoDiscovery_WithSpecificAssembly_ScansOnlyThatAssembly()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);
        var testAssembly = typeof(MongoBuilderTests).Assembly;

        // Act
        builder.WithConfiguratorAutoDiscovery([testAssembly]);

        // Assert
        builder.DiscoveredConfigurators.Should().Contain(typeof(TestConfigurator));
        builder.DiscoveredConfigurators.Should().Contain(typeof(AnotherTestConfigurator));
    }

    [Test]
    public void WithConfigurator_AfterAutoDiscovery_DoesNotRegisterDuplicates()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);
        var testAssembly = typeof(MongoBuilderTests).Assembly;

        // Act
        builder.WithConfiguratorAutoDiscovery([testAssembly]);
        builder.WithConfigurator<TestConfigurator>(); // Already discovered

        // Assert
        var provider = services.BuildServiceProvider();
        var configurators = provider.GetServices<IMongoConfigurator>().ToList();
        var testConfiguratorCount = configurators.Count(c => c is TestConfigurator);

        // Should still only be registered once
        testConfiguratorCount.Should().Be(1);
    }

    [Test]
    public void WithConfigurator_RegistersConfiguratorAsTransient()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);

        // Act
        builder.WithConfigurator<TestConfigurator>();

        // Assert
        var provider = services.BuildServiceProvider();
        var configurators = provider.GetServices<IMongoConfigurator>().ToList();
        configurators.Should().HaveCount(1);
        configurators.Should().AllBeOfType<TestConfigurator>();
    }

    [Test]
    public void WithConfigurator_ReturnsBuilderForChaining()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);

        // Act
        var result = builder.WithConfigurator<TestConfigurator>();

        // Assert
        result.Should().BeSameAs(builder);
    }

    [Test]
    public void WithConfigurator_WithMultipleConfigurators_RegistersAll()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new MongoBuilder(services);

        // Act
        builder.WithConfigurator<TestConfigurator>()
               .WithConfigurator<AnotherTestConfigurator>();

        // Assert
        var provider = services.BuildServiceProvider();
        var configurators = provider.GetServices<IMongoConfigurator>().ToList();
        configurators.Should().HaveCount(2);
        configurators.Should().ContainItemsAssignableTo<IMongoConfigurator>();
    }
}

// Test configurators for testing purposes
public class TestConfigurator : IMongoConfigurator
{
    public Task ConfigureAsync(IMongoHelper helper, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public class AnotherTestConfigurator : IMongoConfigurator
{
    public Task ConfigureAsync(IMongoHelper helper, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
