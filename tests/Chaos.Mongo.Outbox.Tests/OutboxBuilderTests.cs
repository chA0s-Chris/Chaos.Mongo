// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox.Tests;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

public class OutboxBuilderTests
{
    [Test]
    public void Build_DefaultValues_AreCorrect()
    {
        var builder = new OutboxBuilder();
        var options = builder.Build();

        options.CollectionName.Should().Be(OutboxOptions.DefaultCollectionName);
        options.MaxRetries.Should().Be(OutboxOptions.DefaultMaxRetries);
        options.RetryBackoffInitialDelay.Should().Be(OutboxOptions.DefaultRetryBackoffInitialDelay);
        options.RetryBackoffMaxDelay.Should().Be(OutboxOptions.DefaultRetryBackoffMaxDelay);
        options.RetentionPeriod.Should().BeNull();
        options.BatchSize.Should().Be(OutboxOptions.DefaultBatchSize);
        options.PollingInterval.Should().Be(OutboxOptions.DefaultPollingInterval);
        options.LockTimeout.Should().Be(OutboxOptions.DefaultLockTimeout);
        options.AutoStartProcessor.Should().BeFalse();
        options.MessageTypeLookup.Should().BeEmpty();
    }

    [Test]
    public void FluentChaining_AllMethodsReturnBuilder()
    {
        var builder = new OutboxBuilder();

        var result = builder
                     .WithPublisher<TestOutboxPublisher>()
                     .WithMessage<TestPayload>("TestPayload")
                     .WithCollectionName("MyOutbox")
                     .WithMaxRetries(3)
                     .WithRetryBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1))
                     .WithRetentionPeriod(TimeSpan.FromDays(7))
                     .WithBatchSize(50)
                     .WithPollingInterval(TimeSpan.FromSeconds(2))
                     .WithLockTimeout(TimeSpan.FromMinutes(10))
                     .WithAutoStartProcessor();

        result.Should().BeSameAs(builder);
    }

    [Test]
    public void Validate_NoMessageTypes_ThrowsInvalidOperationException()
    {
        var builder = new OutboxBuilder();
        builder.WithPublisher<TestOutboxPublisher>();

        var act = builder.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage("*message type*");
    }

    [Test]
    public void Validate_NoPublisher_ThrowsInvalidOperationException()
    {
        var builder = new OutboxBuilder();
        builder.WithMessage<TestPayload>();

        var act = builder.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage("*IOutboxPublisher*");
    }

    [Test]
    public void Validate_ValidConfiguration_DoesNotThrow()
    {
        var builder = new OutboxBuilder();
        builder.WithPublisher<TestOutboxPublisher>()
               .WithMessage<TestPayload>();

        var act = builder.Validate;

        act.Should().NotThrow();
    }

    [Test]
    public void WithAutoStartProcessor_SetsOption()
    {
        var builder = new OutboxBuilder();

        builder.WithAutoStartProcessor();

        builder.Build().AutoStartProcessor.Should().BeTrue();
    }

    [Test]
    public void WithBatchSize_ValidValue_SetsOption()
    {
        var builder = new OutboxBuilder();

        builder.WithBatchSize(50);

        builder.Build().BatchSize.Should().Be(50);
    }

    [Test]
    public void WithBatchSize_ZeroOrNegative_ThrowsArgumentOutOfRangeException()
    {
        var builder = new OutboxBuilder();

        var actZero = () => builder.WithBatchSize(0);
        var actNeg = () => builder.WithBatchSize(-5);

        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNeg.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void WithCollectionName_NullOrWhitespace_ThrowsArgumentException()
    {
        var builder = new OutboxBuilder();

        var actNull = () => builder.WithCollectionName(null!);
        var actEmpty = () => builder.WithCollectionName("");
        var actWhitespace = () => builder.WithCollectionName("   ");

        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
        actWhitespace.Should().Throw<ArgumentException>();
    }

    [Test]
    public void WithCollectionName_ValidValue_SetsOption()
    {
        var builder = new OutboxBuilder();

        builder.WithCollectionName("MyOutbox");

        builder.Build().CollectionName.Should().Be("MyOutbox");
    }

    [Test]
    public void WithLockTimeout_ValidValue_SetsOption()
    {
        var builder = new OutboxBuilder();

        builder.WithLockTimeout(TimeSpan.FromMinutes(10));

        builder.Build().LockTimeout.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Test]
    public void WithLockTimeout_ZeroOrNegative_ThrowsArgumentOutOfRangeException()
    {
        var builder = new OutboxBuilder();

        var actZero = () => builder.WithLockTimeout(TimeSpan.Zero);
        var actNeg = () => builder.WithLockTimeout(TimeSpan.FromMinutes(-1));

        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNeg.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void WithMaxRetries_NegativeValue_ThrowsArgumentOutOfRangeException()
    {
        var builder = new OutboxBuilder();

        var act = () => builder.WithMaxRetries(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void WithMaxRetries_ValidValue_SetsOption()
    {
        var builder = new OutboxBuilder();

        builder.WithMaxRetries(3);

        builder.Build().MaxRetries.Should().Be(3);
    }

    [Test]
    public void WithMessage_NoDiscriminator_UsesClassName()
    {
        var builder = new OutboxBuilder();

        builder.WithMessage<TestPayload>();

        var options = builder.Build();
        options.MessageTypeLookup.Should().ContainKey(typeof(TestPayload));
        options.MessageTypeLookup[typeof(TestPayload)].Should().Be("TestPayload");
    }

    [Test]
    public void WithMessage_WhitespaceDiscriminator_ThrowsArgumentException()
    {
        var builder = new OutboxBuilder();

        var act = () => builder.WithMessage<TestPayload>("   ");

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void WithMessage_WithDiscriminator_UsesProvidedDiscriminator()
    {
        var builder = new OutboxBuilder();

        builder.WithMessage<TestPayload>("CustomName");

        builder.Build().MessageTypeLookup[typeof(TestPayload)].Should().Be("CustomName");
    }

    [Test]
    public void WithPollingInterval_ValidValue_SetsOption()
    {
        var builder = new OutboxBuilder();

        builder.WithPollingInterval(TimeSpan.FromSeconds(10));

        builder.Build().PollingInterval.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Test]
    public void WithPollingInterval_ZeroOrNegative_ThrowsArgumentOutOfRangeException()
    {
        var builder = new OutboxBuilder();

        var actZero = () => builder.WithPollingInterval(TimeSpan.Zero);
        var actNeg = () => builder.WithPollingInterval(TimeSpan.FromSeconds(-1));

        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNeg.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void WithPublisher_CustomLifetime_SetsLifetime()
    {
        var builder = new OutboxBuilder();

        builder.WithPublisher<TestOutboxPublisher>(ServiceLifetime.Singleton);

        builder.PublisherLifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Test]
    public void WithPublisher_DefaultLifetime_IsTransient()
    {
        var builder = new OutboxBuilder();

        builder.WithPublisher<TestOutboxPublisher>();

        builder.PublisherLifetime.Should().Be(ServiceLifetime.Transient);
    }

    [Test]
    public void WithPublisher_SetsPublisherType()
    {
        var builder = new OutboxBuilder();

        builder.WithPublisher<TestOutboxPublisher>();

        builder.PublisherType.Should().Be(typeof(TestOutboxPublisher));
    }

    [Test]
    public void WithRetentionPeriod_ValidValue_SetsOption()
    {
        var builder = new OutboxBuilder();

        builder.WithRetentionPeriod(TimeSpan.FromDays(7));

        builder.Build().RetentionPeriod.Should().Be(TimeSpan.FromDays(7));
    }

    [Test]
    public void WithRetentionPeriod_ZeroOrNegative_ThrowsArgumentOutOfRangeException()
    {
        var builder = new OutboxBuilder();

        var actZero = () => builder.WithRetentionPeriod(TimeSpan.Zero);
        var actNeg = () => builder.WithRetentionPeriod(TimeSpan.FromDays(-1));

        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNeg.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void WithRetryBackoff_ValidValues_SetsOptions()
    {
        var builder = new OutboxBuilder();

        builder.WithRetryBackoff(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10));

        var options = builder.Build();
        options.RetryBackoffInitialDelay.Should().Be(TimeSpan.FromSeconds(10));
        options.RetryBackoffMaxDelay.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Test]
    public void WithRetryBackoff_ZeroOrNegativeInitialDelay_ThrowsArgumentOutOfRangeException()
    {
        var builder = new OutboxBuilder();

        var actZero = () => builder.WithRetryBackoff(TimeSpan.Zero, TimeSpan.FromMinutes(1));
        var actNeg = () => builder.WithRetryBackoff(TimeSpan.FromSeconds(-1), TimeSpan.FromMinutes(1));

        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNeg.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void WithRetryBackoff_ZeroOrNegativeMaxDelay_ThrowsArgumentOutOfRangeException()
    {
        var builder = new OutboxBuilder();

        var actZero = () => builder.WithRetryBackoff(TimeSpan.FromSeconds(1), TimeSpan.Zero);
        var actNeg = () => builder.WithRetryBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(-1));

        actZero.Should().Throw<ArgumentOutOfRangeException>();
        actNeg.Should().Throw<ArgumentOutOfRangeException>();
    }
}
