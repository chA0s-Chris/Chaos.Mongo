# Support an additional message eligibility filter in OutboxProcessor

> Issue: [#134](https://github.com/chA0s-Chris/Chaos.Mongo/issues/134)

## Rationale

Applications sharing an outbox need processors that publish only selected
messages. Publisher-side deferral either marks messages processed or consumes
retries, while filtering a fetched batch can starve eligible messages.

Add an optional startup-configured MongoDB filter that narrows both server-side
selection and atomic claiming, preserving existing processing guarantees.

## Acceptance Criteria

- [x] An optional ProcessingFilter is available through OutboxOptions and the
      existing WithOutbox builder configuration. Omitting it preserves existing
      behavior.
- [x] Selection applies the filter before sorting and limiting; atomic claiming
      rechecks it alongside message identity and all existing eligibility
      predicates. The filter cannot bypass pending-state, retry-schedule, or
      lock rules.
- [x] Excluded messages are not published or modified by that processor,
      including their state, retry count, retry schedule, and lock fields.
- [x] An excluded backlog larger than BatchSize does not prevent matching
      messages from being processed.
- [x] A selected message changed to no longer match before its claim is neither
      claimed nor published. Previously excluded messages are delivered when
      an appropriately configured processor runs.
- [x] Automated tests cover configuration propagation, filter composition,
      exclusion, backlog starvation, and the selection-to-claim race. Existing
      retry, failure, cancellation, failed-claim, and ownership tests pass.
      Release builds pass and merged line coverage remains at least 95%.
- [x] Public API documentation and the transactional outbox guide explain
      configuration, startup lifetime, and the distinction from message-type
      registration and publisher routing.

## Technical Details

Add these new public members:

- OutboxOptions.ProcessingFilter:
  FilterDefinition<OutboxMessage>? with an init accessor and null default.
- OutboxBuilder.WithProcessingFilter(FilterDefinition<OutboxMessage> filter):
  returns the builder, rejects null, and replaces any previously configured
  filter.

OutboxBuilder.Build carries the filter into the existing singleton options
registration in MongoBuilderExtensions.WithOutbox. This is startup configuration;
runtime refresh and mutation of the configured filter are outside the contract.

In OutboxProcessor.ProcessBatchAsync and ProcessMessageAsync, AND the optional
filter with each existing predicate. Preserve the current sorting, batch limit,
claim-time clock evaluation, and ownership-token checks. Reword the
failed-claim debug log so it also covers a message that no longer matches the
processing filter. Apply the filter only to selection and claiming: completion,
failure, and cancellation cleanup must still finalize an owned claim even when
the claim changes a filtered field.

Extend configuration and query-contract coverage in
tests/Chaos.Mongo.Outbox.Tests. Put new query-contract tests in a new class
backed by a hand-crafted capturing collection; do not add tests that rely on
the existing Moq fixtures. Verify that the rendered application filter,
including a compound one, appears as a top-level conjunct of the rendered
selection and claim filters, whether the driver merges it into the document or
emits `$and`, and never nested inside an `$or`. Leave semantic proof of
conjunction to the integration tests.

Use MongoDB integration tests for persisted exclusion, backlog starvation,
later delivery, and deterministic claim revalidation. For the race, select two
ordered matching messages in one batch; a coordinated test publisher pauses the
first while the test changes the second message's discriminator in MongoDB,
then allows processing to continue. Assert the second message is untouched by
the processor. Exercise processor lifecycle APIs without adding production
test hooks.

Update docs/transactional-outbox.md with a discriminator-filter example and
builder-option entry. Explain that WithMessage registers serialization and
write-side discriminators, while IOutboxPublisher controls delivery routing.
Built-in indexes stay unchanged. The guide states that the filter is not
covered by the polling index, so applications with a large excluded pending
backlog may add their own index.
