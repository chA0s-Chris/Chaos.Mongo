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
}
