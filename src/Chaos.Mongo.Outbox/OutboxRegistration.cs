// Copyright (c) 2025 Christian Flessa. All rights reserved.
// This file is licensed under the MIT license. See LICENSE in the project root for more information.
namespace Chaos.Mongo.Outbox;

using Microsoft.Extensions.DependencyInjection;

internal sealed class OutboxRegistration
{
    public OutboxRegistration(Type? markerType, OutboxOptions options)
    {
        MarkerType = markerType;
        Options = options;
    }

    public Type? MarkerType { get; }
    public OutboxOptions Options { get; }

    public OutboxConfigurator GetConfigurator(IServiceProvider services)
        => MarkerType is null
            ? services.GetRequiredService<OutboxConfigurator>()
            : services.GetRequiredKeyedService<OutboxConfigurator>(this);

    public IOutboxProcessor GetProcessor(IServiceProvider services)
        => MarkerType is null
            ? services.GetRequiredService<IOutboxProcessor>()
            : services.GetRequiredKeyedService<OutboxProcessor>(this);
}
