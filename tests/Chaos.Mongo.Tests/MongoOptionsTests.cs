// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Tests;

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Moq;
using NUnit.Framework;

public class MongoOptionsTests
{
    [Test]
    public void AddMongo_BindConfiguration_BindsAndValidates()
    {
        // Arrange
        var dict = new Dictionary<String, String?>
        {
            ["Mongo:Url"] = "mongodb://localhost:27017/confdb",
            ["Mongo:UseDefaultCollectionNames"] = "true"
        };
        IConfiguration config = new ConfigurationBuilder()
                                .AddInMemoryCollection(dict)
                                .Build();

        var services = new ServiceCollection();
        services.AddMongo(config, "Mongo");

        // Act
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MongoOptions>>().Value;

        // Assert
        options.Url.Should().NotBeNull();
        options.Url!.DatabaseName.Should().Be("confdb");
        options.UseDefaultCollectionNames.Should().BeTrue();
    }

    [Test]
    public void AddMongo_WhenCollectionTypeMapHasEmptyName_FailsValidation()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMongo(o =>
        {
            o.Url = new MongoUrl("mongodb://localhost:27017/test");
            o.CollectionTypeMap[typeof(String)] = String.Empty;
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MongoOptions>>();

        // Act
        var act = () => _ = options.Value;

        // Assert
        act.Should().Throw<OptionsValidationException>()
           .WithMessage("*invalid (null/empty) collection name*");
    }

    [Test]
    public void AddMongo_WhenUrlMissing_ThrowsOptionsValidationException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMongo(); // no Url configured

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MongoOptions>>();

        // Act
        var act = () => _ = options.Value;

        // Assert
        act.Should().Throw<OptionsValidationException>()
           .WithMessage("*MongoOptions.Url must be configured*");
    }

    [Test]
    public void AddMongo_WithConnectionStringAndDatabaseName_SetsDefaultDatabaseAndPassesToFactory()
    {
        // Arrange
        const String connectionString = "mongodb://localhost:27017"; // no database in URL
        const String databaseName = "overrideDb";

        var factoryMock = new Mock<IMongoConnectionFactory>(MockBehavior.Strict);
        var connectionMock = new Mock<IMongoConnection>(MockBehavior.Loose);
        factoryMock
            .Setup(f => f.CreateConnection(It.Is<MongoUrl>(u => u.Url.StartsWith("mongodb://localhost")), databaseName))
            .Returns(connectionMock.Object)
            .Verifiable();

        var services = new ServiceCollection();
        services.AddSingleton(factoryMock.Object); // ensure our mock is used (TryAdd in library)

        services.AddMongo(connectionString, databaseName);

        // Act
        var provider = services.BuildServiceProvider();

        // Resolving IMongoConnection should trigger the factory invocation
        _ = provider.GetRequiredService<IMongoConnection>();

        // Also verify options
        var options = provider.GetRequiredService<IOptions<MongoOptions>>().Value;
        options.Url.Should().NotBeNull();
        options.Url!.DatabaseName.Should().BeNull(); // not provided in URL
        options.DefaultDatabase.Should().Be(databaseName);

        // Assert
        factoryMock.Verify(f => f.CreateConnection(It.IsAny<MongoUrl>(), databaseName), Times.Once);
    }

    [Test]
    public void AddMongo_WithConnectionStringAndServiceProviderConfigure_SetsUrlAndDefaultDatabaseBeforeInvokingConfigure()
    {
        // Arrange
        var services = new ServiceCollection();
        MongoUrl? observedUrl = null;
        String? observedDefaultDatabase = null;
        IServiceProvider? observedServiceProvider = null;

        // Act
        services.AddMongo("mongodb://localhost:27017", "csDb", (serviceProvider, options) =>
        {
            observedServiceProvider = serviceProvider;
            observedUrl = options.Url;
            observedDefaultDatabase = options.DefaultDatabase;
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MongoOptions>>().Value;
        observedServiceProvider.Should().NotBeNull();
        observedUrl.Should().NotBeNull();
        observedUrl!.Server.Host.Should().Be("localhost");
        observedDefaultDatabase.Should().Be("csDb");
        options.DefaultDatabase.Should().Be("csDb");
    }

    [Test]
    public void AddMongo_WithConnectionStringAndServiceProviderConfigure_WhenConfigureIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddMongo("mongodb://localhost:27017/test", null, (Action<IServiceProvider, MongoOptions>)null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
           .WithParameterName("configure");
        services.Should().BeEmpty();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void AddMongo_WithConnectionStringAndServiceProviderConfigure_WhenConnectionStringIsInvalid_ThrowsArgumentException(String? connectionString)
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddMongo(connectionString!, null, (_, _) => { });

        // Assert
        act.Should().Throw<ArgumentException>()
           .WithParameterName("connectionString");
    }

    [Test]
    public void AddMongo_WithConnectionString_RegistersUrlAndValidates()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMongo("mongodb://localhost:27017/mydb");

        // Act
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MongoOptions>>().Value;

        // Assert
        options.Url.Should().NotBeNull();
        options.Url!.DatabaseName.Should().Be("mydb");
    }

    [Test]
    public void AddMongo_WithMongoUrlAndDatabaseName_SetsDefaultDatabaseAndPassesToFactory()
    {
        // Arrange
        var url = new MongoUrl("mongodb://localhost:27017"); // no database in URL
        const String databaseName = "urlOverrideDb";

        var factoryMock = new Mock<IMongoConnectionFactory>(MockBehavior.Strict);
        var connectionMock = new Mock<IMongoConnection>(MockBehavior.Loose);
        factoryMock
            .Setup(f => f.CreateConnection(url, databaseName))
            .Returns(connectionMock.Object)
            .Verifiable();

        var services = new ServiceCollection();
        services.AddSingleton(factoryMock.Object);

        services.AddMongo(url, databaseName);

        // Act
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IMongoConnection>();

        var options = provider.GetRequiredService<IOptions<MongoOptions>>().Value;
        options.Url.Should().NotBeNull();
        options.Url!.DatabaseName.Should().BeNull();
        options.DefaultDatabase.Should().Be(databaseName);

        // Assert
        factoryMock.Verify(f => f.CreateConnection(url, databaseName), Times.Once);
    }

    [Test]
    public void AddMongo_WithMongoUrlAndServiceProviderConfigure_SetsUrlAndDefaultDatabaseBeforeInvokingConfigure()
    {
        // Arrange
        var url = new MongoUrl("mongodb://localhost:27017");
        var services = new ServiceCollection();
        MongoUrl? observedUrl = null;
        String? observedDefaultDatabase = null;

        // Act
        services.AddMongo(url, "urlDb", (_, options) =>
        {
            observedUrl = options.Url;
            observedDefaultDatabase = options.DefaultDatabase;
        });

        // Assert
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<MongoOptions>>().Value;
        observedUrl.Should().BeSameAs(url);
        observedDefaultDatabase.Should().Be("urlDb");
        options.DefaultDatabase.Should().Be("urlDb");
    }

    [Test]
    public void AddMongo_WithMongoUrlAndServiceProviderConfigure_WhenConfigureIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddMongo(new MongoUrl("mongodb://localhost:27017/test"), null, (Action<IServiceProvider, MongoOptions>)null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
           .WithParameterName("configure");
        services.Should().BeEmpty();
    }

    [Test]
    public void AddMongo_WithMongoUrlAndServiceProviderConfigure_WhenUrlIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddMongo((MongoUrl)null!, null, (_, _) => { });

        // Assert
        act.Should().Throw<ArgumentNullException>()
           .WithParameterName("url");
    }

    [Test]
    public void AddMongo_WithServiceProviderConfigure_ResolvesRegisteredService()
    {
        // Arrange
        var url = new MongoUrl("mongodb://localhost:27017/fromService");
        var services = new ServiceCollection();
        services.AddSingleton(url);

        // Act
        services.AddMongo((serviceProvider, options) => options.Url = serviceProvider.GetRequiredService<MongoUrl>());

        // Assert
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<MongoOptions>>().Value;
        options.Url.Should().BeSameAs(url);
    }

    [Test]
    public void AddMongo_WithServiceProviderConfigure_WhenConfigureIsNull_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var act = () => services.AddMongo((Action<IServiceProvider, MongoOptions>)null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
           .WithParameterName("configure");
        services.Should().BeEmpty();
    }

    [Test]
    public void AddMongo_WithServiceProviderConfigure_WhenUrlIsMissing_FailsStartupValidation()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMongo((_, options) => options.DefaultDatabase = "noUrl");
        var startupValidator = services.BuildServiceProvider().GetRequiredService<IStartupValidator>();

        // Act
        var act = () => startupValidator.Validate();

        // Assert
        act.Should().Throw<OptionsValidationException>()
           .WithMessage("*MongoOptions.Url must be configured*");
    }

    [Test]
    public void AddMongo_WithUrlConfigured_Succeeds()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMongo(new MongoUrl("mongodb://localhost:27017/test"));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MongoOptions>>();

        // Act
        var value = options.Value;

        // Assert
        value.Url.Should().NotBeNull();
        value.Url!.Server.Host.Should().Be("localhost");
        value.Url.DatabaseName.Should().Be("test");
    }

    [Test]
    public void Validate_NullCollectionTypeMap_ReturnsValidationFailure()
    {
        var validator = new MongoOptionsValidation();
        var options = new MongoOptions
        {
            Url = new MongoUrl("mongodb://localhost:27017/test"),
            CollectionTypeMap = null!
        };

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain("MongoOptions.CollectionTypeMap must not be null");
    }

    [Test]
    public void Validate_NullOptions_ReturnsValidationFailure()
    {
        var validator = new MongoOptionsValidation();

        var result = validator.Validate(null, null);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain("Options instance is null");
    }
}
