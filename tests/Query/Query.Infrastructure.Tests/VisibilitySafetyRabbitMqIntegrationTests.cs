using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Contracts;
using Aggregator.Query.Application;
using Aggregator.Query.Infrastructure;
using Aggregator.Query.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using RabbitMQ.Client;

namespace Query.Infrastructure.Tests;

public sealed class VisibilitySafetyRabbitMqIntegrationTests
{
    private const string BrokerEnvironmentVariable =
        "PLATFORM_MESSAGING_TEST_RABBITMQ";
    private const string CatalogKey = "berlin-recording-services";
    private const string Digest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 7, 19, 0, 0, TimeSpan.Zero);
    private static readonly Guid BaseProjectionId =
        Guid.Parse("01990200-0000-7000-8000-000000000001");
    private static readonly Guid PromotionOverlayId =
        Guid.Parse("01990200-0000-7000-8000-000000000002");
    private static readonly Guid InitialSafetyOverlayId =
        Guid.Parse("01990200-0000-7000-8000-000000000003");
    private static readonly Guid InitialPublicReadRevisionId =
        Guid.Parse("01990200-0000-7000-8000-000000000004");
    private static readonly Guid SourcePublicationId =
        Guid.Parse("01990200-0000-7000-8000-000000000005");
    private static readonly Guid VisibilityEventId =
        Guid.Parse("01990200-0000-7000-8000-000000000010");
    private static readonly Guid SuppressionId =
        Guid.Parse("01990200-0000-7000-8000-000000000011");
    private static readonly Guid MaterializedSafetyOverlayId =
        Guid.Parse("01990200-0000-7000-8000-000000000020");
    private static readonly Guid MaterializedPublicReadRevisionId =
        Guid.Parse("01990200-0000-7000-8000-000000000021");
    private static readonly JsonSerializerOptions SerializerOptions =
        CreateSerializerOptions();

    [Fact]
    public async Task TransientFailureKeepsPublicReadBlockedUntilRabbitMqRedeliveryCompletes()
    {
        var brokerUri = new Uri(RequireBroker(), UriKind.Absolute);
        var suffix = Guid.NewGuid().ToString("N");
        var options = new QueryVisibilityWorkerOptions
        {
            BrokerUri = brokerUri,
            Exchange = $"query.visibility.integration.events.{suffix}",
            Queue = $"query.visibility.integration.queue.{suffix}",
            DeadLetterExchange = $"query.visibility.integration.dead-letter.{suffix}",
            DeadLetterQueue = $"query.visibility.integration.dead-letter.queue.{suffix}",
            PrefetchCount = 1,
            DeliveryLimit = 4,
            RetryDelay = TimeSpan.FromSeconds(5),
        };
        await using var database = await QueryPostgresTestDatabase.CreateAsync();
        await database.ApplyAllQueryMigrationsAsync();
        var logger = new SignalLogger<VisibilitySafetyProjectionWorker>();
        var services = new ServiceCollection();
        services.AddSingleton(_ => NpgsqlDataSource.Create(database.ConnectionString));
        services.AddSingleton<IQueryClock>(new FixedQueryClock(Timestamp.AddMinutes(1)));
        services.AddSingleton<IQueryIdFactory>(new QueueQueryIdFactory(
            MaterializedSafetyOverlayId,
            MaterializedPublicReadRevisionId));
        services.AddScoped<IVisibilitySafetyProjectionStore, PostgresVisibilitySafetyProjectionStore>();
        services.AddScoped<VisibilitySafetyProjectionService>();
        await using var serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        var dataSource = serviceProvider.GetRequiredService<NpgsqlDataSource>();
        var worker = new VisibilitySafetyProjectionWorker(
            options,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            logger);
        var connectionFactory = new ConnectionFactory
        {
            Uri = brokerUri,
            AutomaticRecoveryEnabled = false,
            TopologyRecoveryEnabled = false,
            ClientProvidedName = $"query-visibility-integration-setup-{suffix}",
        };
        await using var brokerConnection = await connectionFactory.CreateConnectionAsync(
            connectionFactory.ClientProvidedName,
            CancellationToken.None);
        await using var brokerChannel = await brokerConnection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            CancellationToken.None);
        await DeclareTopologyAsync(brokerChannel, options);
        var change = CreateChange();
        var payload = JsonSerializer.SerializeToUtf8Bytes(change, SerializerOptions);
        var payloadDigest = Convert
            .ToHexString(SHA256.HashData(payload))
            .ToLowerInvariant();
        await PublishAsync(
            brokerChannel,
            options,
            change,
            payload,
            payloadDigest);

        var workerStarted = false;
        try
        {
            await worker.StartAsync(CancellationToken.None);
            workerStarted = true;
            var firstFailure = await logger.FirstWarning.WaitAsync(TimeSpan.FromSeconds(15));
            var projectionFailure = Assert.IsType<QueryProjectionException>(firstFailure);
            Assert.Equal("QUERY_VISIBILITY_PUBLIC_READ_MISSING", projectionFailure.Code);
            Assert.Equal(503, projectionFailure.StatusCode);
            Assert.Equal(1L, await CountPendingBlockAsync(database));
            Assert.Equal("pending", await ReadInboxStateAsync(database));

            await SeedCurrentReadAsync(database);
            var publicStore = new SafetyAwarePublicQueryStore(
                new NpgsqlPublicQueryStore(dataSource),
                dataSource,
                new FixedQueryClock(Timestamp.AddMinutes(2)));
            var blocked = await Assert.ThrowsAsync<QueryReadException>(() =>
                publicStore.ReadPageAsync(
                    CatalogKey,
                    afterListingId: null,
                    maximumDocuments: 10,
                    categoryKey: null,
                    requestedLocale: "de-DE",
                    Timestamp.AddMinutes(2),
                    CancellationToken.None));
            Assert.Equal("Query.VisibilitySafety", blocked.Owner);
            Assert.Equal("QUERY_VISIBILITY_UPDATE_PENDING", blocked.Code);
            Assert.Equal(503, blocked.StatusCode);
            Assert.Equal(
                VisibilityEventId,
                Assert.IsType<Guid>(blocked.Context["sourceEventId"]));

            await EventuallyAsync(
                async () => string.Equals(
                    await ReadInboxStateAsync(database),
                    "completed",
                    StringComparison.Ordinal),
                "RabbitMQ redelivery did not complete the pending visibility event.");

            Assert.Equal(0L, await CountPendingBlockAsync(database));
            Assert.Equal(
                MaterializedPublicReadRevisionId.ToString("D"),
                await database.ScalarAsync<string>(
                    """
                    SELECT current_read.public_read_revision_id::text
                    FROM projection.current_public_read AS current_read
                    WHERE current_read.catalog_key = @catalog_key;
                    """,
                    new NpgsqlParameter<string>("catalog_key", CatalogKey)));
            Assert.Equal(
                2L,
                await database.ScalarAsync<long>(
                    """
                    SELECT current_read.activation_revision
                    FROM projection.current_public_read AS current_read
                    WHERE current_read.catalog_key = @catalog_key;
                    """,
                    new NpgsqlParameter<string>("catalog_key", CatalogKey)));
            Assert.Equal(
                MaterializedPublicReadRevisionId.ToString("D"),
                await database.ScalarAsync<string>(
                    """
                    SELECT inbox.result_public_read_revision_id::text
                    FROM messaging.visibility_suppression_inbox_message AS inbox
                    WHERE inbox.event_id = @event_id;
                    """,
                    new NpgsqlParameter<Guid>("event_id", VisibilityEventId)));
            Assert.Equal(
                1L,
                await database.ScalarAsync<long>(
                    """
                    SELECT count(*)
                    FROM projection.visibility_safety_overlay_item AS item
                    WHERE item.overlay_id = @overlay_id
                      AND item.suppression_id = @suppression_id
                      AND item.target_kind = 'route';
                    """,
                    new NpgsqlParameter<Guid>("overlay_id", MaterializedSafetyOverlayId),
                    new NpgsqlParameter<Guid>("suppression_id", SuppressionId)));

            var finalPage = Assert.IsType<PublicReadPageSnapshot>(
                await publicStore.ReadPageAsync(
                    CatalogKey,
                    afterListingId: null,
                    maximumDocuments: 10,
                    categoryKey: null,
                    requestedLocale: "de-DE",
                    Timestamp.AddMinutes(2),
                    CancellationToken.None));
            Assert.Equal(MaterializedPublicReadRevisionId, finalPage.Revision.Id);
            Assert.Equal(MaterializedSafetyOverlayId, finalPage.Revision.SafetyOverlayId);
            Assert.Empty(finalPage.Documents);
            Assert.Null(await brokerChannel.BasicGetAsync(
                options.DeadLetterQueue,
                autoAck: true,
                CancellationToken.None));
        }
        finally
        {
            try
            {
                if (workerStarted)
                {
                    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await worker.StopAsync(stopTimeout.Token);
                }
            }
            finally
            {
                worker.Dispose();
                await DeleteTopologyAsync(brokerChannel, options);
            }
        }
    }

    private static CatalogPublicVisibilitySuppressionChanged CreateChange() =>
        new(
            VisibilityEventId,
            SuppressionId,
            CatalogKey,
            new PublicVisibilitySuppressionTargetContract(
                PublicVisibilitySuppressionTargetKindContract.Route,
                ListingId: null,
                "/legal-removal"),
            "legal-removal",
            PublicVisibilitySuppressionResponseModeContract.Gone,
            PublicVisibilitySuppressionStateContract.Active,
            Timestamp,
            ExpiresAtUtc: null,
            AggregateRevision: 2,
            Timestamp);

    private static async Task PublishAsync(
        IChannel channel,
        QueryVisibilityWorkerOptions options,
        CatalogPublicVisibilitySuppressionChanged change,
        ReadOnlyMemory<byte> payload,
        string payloadDigest)
    {
        var properties = new BasicProperties
        {
            AppId = "query-visibility-integration-test",
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = change.EventId.ToString("D"),
            Type = CatalogIntegrationEventContracts.PublicVisibilitySuppressionChanged,
            Timestamp = new AmqpTimestamp(change.OccurredAtUtc.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["payload-digest"] = payloadDigest,
            },
        };
        await channel.BasicPublishAsync(
            options.Exchange,
            options.RoutingKey,
            mandatory: true,
            properties,
            payload,
            CancellationToken.None);
    }

    private static async Task DeclareTopologyAsync(
        IChannel channel,
        QueryVisibilityWorkerOptions options)
    {
        await channel.ExchangeDeclareAsync(
            options.Exchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            noWait: false,
            CancellationToken.None);
        await channel.ExchangeDeclareAsync(
            options.DeadLetterExchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            noWait: false,
            CancellationToken.None);
        await channel.QueueDeclareAsync(
            options.DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x-queue-type"] = "quorum",
            },
            CancellationToken.None);
        await channel.QueueBindAsync(
            options.DeadLetterQueue,
            options.DeadLetterExchange,
            options.RoutingKey,
            arguments: null,
            cancellationToken: CancellationToken.None);
        await channel.QueueDeclareAsync(
            options.Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = options.DeliveryLimit,
                ["x-dead-letter-exchange"] = options.DeadLetterExchange,
                ["x-dead-letter-routing-key"] = options.RoutingKey,
            },
            CancellationToken.None);
        await channel.QueueBindAsync(
            options.Queue,
            options.Exchange,
            options.RoutingKey,
            arguments: null,
            cancellationToken: CancellationToken.None);
    }

    private static async Task DeleteTopologyAsync(
        IChannel channel,
        QueryVisibilityWorkerOptions options)
    {
        _ = await channel.QueueDeleteAsync(
            options.Queue,
            ifUnused: false,
            ifEmpty: false,
            noWait: false,
            cancellationToken: CancellationToken.None);
        _ = await channel.QueueDeleteAsync(
            options.DeadLetterQueue,
            ifUnused: false,
            ifEmpty: false,
            noWait: false,
            cancellationToken: CancellationToken.None);
        await channel.ExchangeDeleteAsync(
            options.Exchange,
            ifUnused: false,
            noWait: false,
            cancellationToken: CancellationToken.None);
        await channel.ExchangeDeleteAsync(
            options.DeadLetterExchange,
            ifUnused: false,
            noWait: false,
            cancellationToken: CancellationToken.None);
    }

    private static async Task EventuallyAsync(
        Func<Task<bool>> condition,
        string failureMessage)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new InvalidOperationException(failureMessage);
    }

    private static Task<long> CountPendingBlockAsync(
        QueryPostgresTestDatabase database) =>
        database.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM projection.catalog_visibility_block AS block
            WHERE block.source_event_id = @event_id
              AND block.catalog_key = @catalog_key;
            """,
            new NpgsqlParameter<Guid>("event_id", VisibilityEventId),
            new NpgsqlParameter<string>("catalog_key", CatalogKey));

    private static Task<string> ReadInboxStateAsync(
        QueryPostgresTestDatabase database) =>
        database.ScalarAsync<string>(
            """
            SELECT inbox.processing_state
            FROM messaging.visibility_suppression_inbox_message AS inbox
            WHERE inbox.event_id = @event_id;
            """,
            new NpgsqlParameter<Guid>("event_id", VisibilityEventId));

    private static Task SeedCurrentReadAsync(QueryPostgresTestDatabase database) =>
        database.ExecuteAsync(
            """
            INSERT INTO projection.base_projection
            (
                id,
                catalog_key,
                default_locale,
                supported_locales,
                source_publication_id,
                source_publication_digest,
                source_publication_sequence,
                builder_identity,
                created_at_utc,
                content_digest
            )
            VALUES
            (
                @base_projection_id,
                @catalog_key,
                'de-DE',
                ARRAY['de-DE']::text[],
                @source_publication_id,
                @digest,
                1,
                'query-projection-builder@1',
                @timestamp,
                @digest
            );

            INSERT INTO projection.overlay_revision
            (
                id,
                catalog_key,
                kind,
                source_revision,
                created_at_utc,
                content_digest,
                item_count
            )
            VALUES
            (
                @promotion_overlay_id,
                @catalog_key,
                'promotion',
                0,
                @timestamp,
                @digest,
                0
            ),
            (
                @safety_overlay_id,
                @catalog_key,
                'visibility_safety',
                0,
                @timestamp,
                @digest,
                0
            );

            INSERT INTO projection.public_read_revision
            (
                id,
                catalog_key,
                base_projection_id,
                promotion_overlay_id,
                safety_overlay_id,
                source_publication_id,
                created_at_utc,
                content_digest
            )
            VALUES
            (
                @public_read_revision_id,
                @catalog_key,
                @base_projection_id,
                @promotion_overlay_id,
                @safety_overlay_id,
                @source_publication_id,
                @timestamp,
                @digest
            );

            INSERT INTO projection.current_public_read
            (
                catalog_key,
                public_read_revision_id,
                activation_revision,
                activated_at_utc
            )
            VALUES
            (
                @catalog_key,
                @public_read_revision_id,
                1,
                @timestamp
            );
            """,
            new NpgsqlParameter<Guid>("base_projection_id", BaseProjectionId),
            new NpgsqlParameter<Guid>("promotion_overlay_id", PromotionOverlayId),
            new NpgsqlParameter<Guid>("safety_overlay_id", InitialSafetyOverlayId),
            new NpgsqlParameter<Guid>("public_read_revision_id", InitialPublicReadRevisionId),
            new NpgsqlParameter<Guid>("source_publication_id", SourcePublicationId),
            new NpgsqlParameter<string>("catalog_key", CatalogKey),
            new NpgsqlParameter<string>("digest", Digest),
            QueryPostgresTestDatabase.UtcParameter("timestamp", Timestamp));

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }

    private static string RequireBroker()
    {
        var value = Environment.GetEnvironmentVariable(BrokerEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException(
                $"Environment variable '{BrokerEnvironmentVariable}' is required for Query visibility RabbitMQ proof.");
    }

    private sealed class QueueQueryIdFactory(params Guid[] values) : IQueryIdFactory
    {
        private readonly Queue<Guid> _values = new(values);

        public Guid Create() =>
            _values.Count > 0
                ? _values.Dequeue()
                : throw new InvalidOperationException(
                    "Query visibility integration ID sequence is exhausted.");
    }

    private sealed class FixedQueryClock(DateTimeOffset timestamp) : IQueryClock
    {
        public DateTimeOffset GetUtcNow() => timestamp;
    }

    private sealed class SignalLogger<T> : ILogger<T>
    {
        private readonly TaskCompletionSource<Exception?> _firstWarning =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Exception?> FirstWarning => _firstWarning.Task;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (logLevel == LogLevel.Warning)
            {
                _firstWarning.TrySetResult(exception);
            }
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static NoopScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
